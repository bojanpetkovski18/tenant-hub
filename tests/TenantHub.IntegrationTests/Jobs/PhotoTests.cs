using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Background;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Jobs;

/// <summary>
/// Photos through the API (US-90, SPEC-V2 5.6, criterion 17): accepted by content only, size checked before decoding,
/// stored re-encoded without metadata, served with safe headers, limited per job and by the plan's storage, and their
/// files removed with their rows.
/// </summary>
public sealed class PhotoTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string> Refused() => new()
    {
        // what, file name, declared type
        { "script", "photo.jpg", "image/jpeg" },
        { "svg", "photo.svg", "image/svg+xml" },
        { "html then jpeg", "photo.jpg", "image/jpeg" },
        { "empty", "photo.jpg", "image/jpeg" },
        { "truncated jpeg", "photo.jpg", "image/jpeg" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Content_that_is_not_an_allowed_image_is_refused_whatever_its_name_or_type(string what, string name, string type)
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var content = what switch
        {
            "script" => TestImages.Script,
            "svg" => TestImages.Svg,
            "html then jpeg" => TestImages.HtmlThenJpeg(),
            "empty" => [],
            _ => TestImages.Jpeg(400, 300)[..600],
        };
        var member = await tenants.ClientForAsync(Role.Member);

        var response = await member.UploadAsync(Url(tenants, job), content, name, type);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "photo-not-accepted");
        Assert.Empty(await PhotosOfAsync(tenants, job));
        Assert.Empty(FilesOf(tenants, tenants.OrgA, except: await KnownKeysAsync(tenants)));
    }

    [Fact]
    public async Task A_png_sent_as_a_jpeg_is_stored_and_served_as_what_it_is()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);

        var created = await member.UploadAsync(Url(tenants, job), TestImages.Png(), "holiday.jpg", "image/jpeg");
        var photo = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var served = await member.Http.GetAsync($"{Url(tenants, job)}/{photo.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("image/png", photo.GetProperty("contentType").GetString());
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(SKEncodedImageFormat.Png, TestImages.Inspect(await served.Content.ReadAsByteArrayAsync(Ct)).Format);
    }

    [Fact]
    public async Task A_jpeg_with_location_and_rotation_is_stored_upright_without_any_metadata()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        var upload = TestImages.WithExif(TestImages.Jpeg(64, 48), orientation: 6);
        Assert.True(TestImages.HasMetadata(upload)); // the test's own file does carry it

        var created = await member.UploadAsync(Url(tenants, job), upload);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = Assert.Single(await PhotosOfAsync(tenants, job));
        var files = tenants.Factory.Services.GetRequiredService<IFileStore>();
        foreach (var key in new[] { row.StorageKey, row.ThumbnailKey })
        {
            await using var stream = (await files.OpenReadAsync(key, Ct))!;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            var stored = copy.ToArray();
            Assert.False(TestImages.HasMetadata(stored), $"{key} still has EXIF or XMP");
            Assert.Equal(-1, TestImages.IndexOf(stored, "ICC_PROFILE"u8));
        }

        // Orientation 6 turned the picture a quarter clockwise: 48 × 64, and the red corner is now top right.
        Assert.Equal((48, 64), (row.Width, row.Height));
        var image = await member.Http.GetByteArrayAsync($"{Url(tenants, job)}/{row.Id}", Ct);
        Assert.True(TestImages.Pixel(image, 40, 8).Red > 200, "the red quarter should be top right");
        Assert.True(TestImages.Pixel(image, 8, 56).Blue > 200, "the bottom left should be blue");
    }

    [Fact]
    public async Task Bytes_after_the_image_are_not_stored()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);

        var created = await member.UploadAsync(Url(tenants, job), TestImages.JpegThenScript());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var served = await member.Http.GetByteArrayAsync($"{Url(tenants, job)}/{id}", Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(-1, TestImages.IndexOf(served, "<script"u8));
    }

    [Fact]
    public async Task An_oversized_file_and_a_decompression_bomb_are_refused_before_decoding()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        byte[] oversized = [.. TestImages.Jpeg(), .. new byte[PhotoRules.MaxUploadBytes]];
        var bomb = TestImages.DecompressionBomb();
        Assert.True(bomb.Length < 200_000, $"the bomb is {bomb.Length} bytes");

        var tooBig = await member.UploadAsync(Url(tenants, job), oversized);
        var timer = Stopwatch.StartNew();
        var exploded = await member.UploadAsync(Url(tenants, job), bomb, "bomb.png", "image/png");
        timer.Stop();

        await ProblemAssert.HasCodeAsync(tooBig, HttpStatusCode.RequestEntityTooLarge, "photo-too-large");
        var problem = await ProblemAssert.IsProblemAsync(exploded, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("photo-too-large", problem.GetProperty("code").GetString());
        Assert.Contains("too many pixels", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        // Decoding 20,000 × 20,000 pixels would take gigabytes and seconds; reading the header takes neither.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Empty(await PhotosOfAsync(tenants, job));
    }

    [Fact]
    public async Task Photos_are_served_inline_under_our_name_with_nosniff_and_a_private_cache()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        var viewer = await tenants.ClientForAsync(Role.Viewer);
        var id = (await (await member.UploadAsync(Url(tenants, job), TestImages.Webp(), "../../etc/passwd", "text/html")).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var full = await viewer.Http.GetAsync($"{Url(tenants, job)}/{id}", Ct);
        var thumbnail = await viewer.Http.GetAsync($"{Url(tenants, job)}/{id}/thumbnail", Ct);
        var list = await viewer.Http.GetFromJsonAsync<JsonElement>(Url(tenants, job), Ct);

        foreach (var response in new[] { full, thumbnail })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Matches("^JOB-\\d{4}-photo(-thumbnail)?\\.webp$", response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
            Assert.Contains("private", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }

        var (width, height, _) = TestImages.Inspect(await thumbnail.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(64, width); // already small: the thumbnail is never larger than the photo
        Assert.Equal(48, height);
        Assert.Equal((false, false), (list.GetProperty("canAdd").GetBoolean(), list.GetProperty("canRemove").GetBoolean()));
    }

    [Fact]
    public async Task A_large_photo_is_stored_at_most_2560_pixels_with_a_320_pixel_thumbnail()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);

        (await member.UploadAsync(Url(tenants, job), TestImages.Jpeg(4000, 3000))).EnsureSuccessStatusCode();

        var row = Assert.Single(await PhotosOfAsync(tenants, job));
        Assert.Equal((2560, 1920), (row.Width, row.Height));
        var thumbnail = await member.Http.GetByteArrayAsync($"{Url(tenants, job)}/{row.Id}/thumbnail", Ct);
        Assert.Equal((320, 240), (TestImages.Inspect(thumbnail).Width, TestImages.Inspect(thumbnail).Height));
    }

    [Fact]
    public async Task Only_whoever_works_on_an_open_job_adds_photos_and_a_job_has_at_most_ten()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var mine = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var someoneElses = await tenants.CreateJobAsync(tenants.OrgA, Role.Admin);
        var member = await tenants.ClientForAsync(Role.Member);
        var viewer = await tenants.ClientForAsync(Role.Viewer);
        for (var i = 0; i < Checklists.MaxPhotos; i++)
        {
            (await member.UploadAsync(Url(tenants, mine), TestImages.Jpeg())).EnsureSuccessStatusCode();
        }

        await ProblemAssert.HasCodeAsync(await member.UploadAsync(Url(tenants, mine), TestImages.Jpeg()), HttpStatusCode.Conflict, "photo-limit-reached");
        await ProblemAssert.HasCodeAsync(await member.UploadAsync(Url(tenants, someoneElses), TestImages.Jpeg()), HttpStatusCode.Forbidden, "job-not-yours");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.UploadAsync(Url(tenants, mine), TestImages.Jpeg())).StatusCode);

        var owner = await tenants.ClientForAsync(Role.Owner);
        (await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs/{someoneElses}/transitions", new { to = "cancelled", reason = "Not needed" })).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await owner.UploadAsync(Url(tenants, someoneElses), TestImages.Jpeg()), HttpStatusCode.Conflict, "job-closed");
        Assert.Equal(Checklists.MaxPhotos, (await PhotosOfAsync(tenants, mine)).Count);
    }

    [Fact]
    public async Task Two_uploads_with_room_for_one_store_exactly_one_and_leave_no_files_behind()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        await tenants.SetPlanAsync(tenants.OrgA, Plan.Free);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        var size = tenants.Factory.Services.GetRequiredService<IPhotoProcessor>().Process(TestImages.Jpeg()).Bytes;
        // Fill the plan's 100 MB to within one and a half photos of the limit.
        await FillStorageAsync(tenants, tenants.OrgA, PlanLimits.For(Plan.Free).StorageBytes!.Value - await StorageUsedAsync(tenants, tenants.OrgA) - size - (size / 2));
        var filesBefore = FilesOf(tenants, tenants.OrgA, except: []).Count;

        var answers = await Task.WhenAll(
            member.UploadAsync(Url(tenants, job), TestImages.Jpeg()),
            member.UploadAsync(Url(tenants, job), TestImages.Jpeg()));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
        var refused = await ProblemAssert.IsProblemAsync(answers.Single(a => a.StatusCode != HttpStatusCode.Created), HttpStatusCode.Forbidden);
        Assert.Equal("plan-limit-reached", refused.GetProperty("code").GetString());
        Assert.Equal("storage", refused.GetProperty("limit").GetString());
        Assert.Single(await PhotosOfAsync(tenants, job));
        // The refused upload's files were written before the check and removed after it.
        Assert.Equal(filesBefore + 2, FilesOf(tenants, tenants.OrgA, except: []).Count);
        var overview = await (await tenants.ClientForAsync(Role.Owner)).Http.GetFromJsonAsync<JsonElement>($"/api/orgs/{tenants.OrgA}", Ct);
        Assert.Equal(100L * 1024 * 1024, overview.GetProperty("limits").GetProperty("storageBytes").GetInt64());
        Assert.Equal(await StorageUsedAsync(tenants, tenants.OrgA), overview.GetProperty("usage").GetProperty("storageBytes").GetInt64());
    }

    [Fact]
    public async Task Removing_a_photo_or_deleting_its_job_removes_the_files_after_the_commit()
    {
        var tenants = await SharedApiTenants.GetAsync<PhotoTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var job = await tenants.CreateJobAsync(tenants.OrgA);
        var other = await tenants.CreateJobAsync(tenants.OrgA);
        var removed = await tenants.CreatePhotoAsync(tenants.OrgA, job);
        await tenants.CreatePhotoAsync(tenants.OrgA, other);
        await tenants.CreatePhotoAsync(tenants.OrgA, other);
        var removedKeys = (await PhotosOfAsync(tenants, job)).SelectMany(p => new[] { p.StorageKey, p.ThumbnailKey }).ToList();
        var jobKeys = (await PhotosOfAsync(tenants, other)).SelectMany(p => new[] { p.StorageKey, p.ThumbnailKey }).ToList();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"{Url(tenants, job)}/{removed}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/jobs/{other}", null)).StatusCode);
        Assert.All(removedKeys.Concat(jobKeys), key => Assert.True(Exists(tenants, key), "the files stay until the outbox runs"));
        await tenants.Factory.DispatchOutboxAsync();

        Assert.All(removedKeys.Concat(jobKeys), key => Assert.False(Exists(tenants, key), $"{key} is still there"));
        Assert.Empty(await PhotosOfAsync(tenants, job));
        await ProblemAssert.HasCodeAsync(await owner.Http.GetAsync($"{Url(tenants, job)}/{removed}", Ct), HttpStatusCode.NotFound, "photo-not-found");
    }

    [Fact]
    public async Task Deleting_an_organization_removes_its_folder_and_nothing_of_another()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var keysOfB = await KeysOfAsync(tenants, tenants.OrgB);
        Assert.NotEmpty(FilesOf(tenants, tenants.OrgA, except: []));

        (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}", new { confirmName = "Alpha Cleaning" })).EnsureSuccessStatusCode();
        await tenants.Factory.DispatchOutboxAsync();

        Assert.False(Directory.Exists(Path.Combine(tenants.Factory.FilesRoot, tenants.OrgA.ToString("N"))));
        Assert.All(keysOfB, key => Assert.True(Exists(tenants, key)));
    }

    [Fact]
    public async Task The_weekly_sweep_removes_old_orphans_and_folders_of_deleted_organizations_and_nothing_else()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var files = tenants.Factory.Services.GetRequiredService<IFileStore>();
        var kept = await KeysOfAsync(tenants, tenants.OrgA);
        var oldOrphan = FileKeys.New(tenants.OrgA, "jpg");
        var newOrphan = FileKeys.New(tenants.OrgA, "jpg");
        var gone = Guid.NewGuid();
        var ofGone = FileKeys.New(gone, "png");
        foreach (var key in new[] { oldOrphan, newOrphan, ofGone })
        {
            await files.WriteAsync(key, TestImages.Jpeg(), Ct);
        }

        File.SetLastWriteTimeUtc(PathOf(tenants, oldOrphan), DateTime.UtcNow.AddDays(-2));
        // Every kept file is old too: being old is not enough to be swept.
        foreach (var key in kept)
        {
            File.SetLastWriteTimeUtc(PathOf(tenants, key), DateTime.UtcNow.AddDays(-30));
        }

        var scheduler = tenants.Factory.Services.GetRequiredService<Scheduler>();
        await scheduler.EnsureTasksAsync(Ct);
        await scheduler.RunDueAsync(Ct);

        Assert.False(Exists(tenants, oldOrphan));
        Assert.True(Exists(tenants, newOrphan), "an upload may still be on its way to its commit");
        Assert.False(Directory.Exists(Path.Combine(tenants.Factory.FilesRoot, gone.ToString("N"))));
        Assert.All(kept, key => Assert.True(Exists(tenants, key), $"{key} has a row"));
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var task = await db.ScheduledTasks.SingleAsync(t => t.Name == "files.sweep", Ct);
        Assert.True(task.DueAt > DateTimeOffset.UtcNow.AddDays(6), "next week");
    }

    private static string Url(ApiTenants tenants, Guid job) => $"/api/orgs/{tenants.OrgA}/jobs/{job}/photos";

    private static async Task<List<Attachment>> PhotosOfAsync(ApiTenants tenants, Guid job)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.Attachments.AcrossTenants().AsNoTracking().Where(a => a.JobId == job).ToListAsync(Ct);
    }

    private static async Task<List<string>> KeysOfAsync(ApiTenants tenants, Guid organization)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var rows = await db.Attachments.AcrossTenants().AsNoTracking().Where(a => a.OrganizationId == organization).ToListAsync(Ct);
        return [.. rows.SelectMany(a => new[] { a.StorageKey, a.ThumbnailKey })];
    }

    private static async Task<HashSet<string>> KnownKeysAsync(ApiTenants tenants) =>
        [.. await KeysOfAsync(tenants, tenants.OrgA)];

    private static async Task<long> StorageUsedAsync(ApiTenants tenants, Guid organization)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.Attachments.AcrossTenants().Where(a => a.OrganizationId == organization).SumAsync(a => (long?)a.Bytes, Ct) ?? 0;
    }

    /// <summary>A row standing for <paramref name="bytes"/> of photos (its files are not needed for the sum).</summary>
    private static async Task FillStorageAsync(ApiTenants tenants, Guid organization, long bytes)
    {
        var job = await tenants.CreateJobAsync(organization);
        await using var lookup = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var ownerId = await lookup.Memberships.AcrossTenants()
            .Where(m => m.OrganizationId == organization && m.Role == Role.Owner && m.RemovedAt == null)
            .Select(m => m.UserId)
            .FirstAsync(Ct);
        var tenant = new TenantContext();
        tenant.Enter(organization, ownerId, Role.Owner);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString, tenant);
        var tracked = await db.Jobs.SingleAsync(j => j.Id == job, Ct);
        var stored = new StoredPhoto(FileKeys.New(organization, "jpg"), FileKeys.New(organization, "jpg"), "image/jpeg", bytes, 1, 1);
        db.Attachments.Add(tracked.AddPhoto(stored, 0, JobActor.From(ownerId, Role.Owner), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The organization's files on disk, other than <paramref name="except"/>.</summary>
    private static List<string> FilesOf(ApiTenants tenants, Guid organization, HashSet<string> except)
    {
        var directory = Path.Combine(tenants.Factory.FilesRoot, organization.ToString("N"));
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory).Select(f => $"{organization:N}/{Path.GetFileName(f)}").Where(k => !except.Contains(k))]
            : [];
    }

    private static string PathOf(ApiTenants tenants, string key) => Path.Combine(tenants.Factory.FilesRoot, key[..32], key[33..]);

    private static bool Exists(ApiTenants tenants, string key) => File.Exists(PathOf(tenants, key));

}
