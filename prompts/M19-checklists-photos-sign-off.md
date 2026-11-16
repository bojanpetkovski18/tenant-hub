# M19: Checklists, photos and client sign-off

Branch: `#0019-checklists-photos-and-client-sign-off`

## Goal
The person doing the work can prove it (checklist and photos, stripped of location data), and the client can see the proof and confirm the visit or report a problem.

## Context
SPEC-V2 4.5, 4.8 (storage limit), 5.6, 7 (checklist, photos, portal visit endpoints), 8 (`checklist_items`, `attachments`, job feedback columns), 9 (uploads), criterion 17; D013, D043. Stories US-90, US-91. CLAUDE.md and the playbook's upload rules (size, dimensions, magic bytes, re-encode, strip metadata).

Research before starting and record with dates (D043): the image library for decoding and re-encoding JPEG, PNG and WebP in .NET 10. Check its licence for this MIT repository, its native dependencies in `aspnet:10.0.x-noble-chiseled-extra`, its open advisories, and its release age (two-week rule). Present the choice to the owner before adding it if the licence or native dependencies are not clear-cut.

## Scope
1. **Checklists.**
   - Domain: items on a job with the rules in 5.6; the Completed precondition (`409 checklist-incomplete` with the open items), tested alongside every v1 transition.
   - Agreements gain a checklist template (M16's form), copied into each generated visit.
   - Endpoints, and a checklist on the job page (tick, untick, add, edit, reorder by keyboard and pointer).
2. **File store.** An `IFileStore` port with a local-directory implementation (a Compose volume, a temp directory in tests); keys are random; no path from user input reaches the file system.
3. **Photos.**
   - The upload endpoint streams with a size limit and checks magic bytes;
   - decodes with dimension limits checked before full decoding;
   - re-encodes without metadata, at most 2,560 px on the long edge, plus a thumbnail;
   - checks the storage limit under the organization lock and writes the row;
   - files are written before commit and removed by the outbox if the transaction fails, or written after commit; decide which and log it.
   - Serving endpoints add `Content-Disposition`, `nosniff` and a private cache.
   - Deletion goes through the outbox, with a weekly orphan sweep on the scheduler.
4. **Storage limit** in `PlanPolicy` (Free 100 MB, Team 5 GB, Business 50 GB) and on the plan page.
5. **Client feedback:** portal confirm and report-problem on completed visits (5.6), shown on the job page and in the audit log; the portal visit page shows the checklist and photos.
6. **Angular:**
   - photo upload from a phone (`accept="image/*"`, camera allowed), with progress, a refusal message and thumbnails;
   - a viewer dialog through `Confirm`/`CspSafeDialogContainer` (D027);
   - images and blob URLs must be allowed by the CSP: adjust `img-src` only as narrowly as needed and record it.
7. **Seed:** some completed Brightline visits get checklists, photos and feedback. The photos are generated images (for example, plain patterns drawn by code), never real photos.

## Out of scope
Video, PDFs or other file types; signatures drawn on screen; S3-compatible storage (only if hosting needs it, with the owner).

## Tests required
- **Upload safety (criterion 17):** a script, an SVG, a polyglot file and a PNG renamed `.jpg` with the wrong declared type: each refused or handled by content; an oversized file and a decompression bomb refused before full decoding; a JPEG with EXIF GPS stored without it (the test reads the stored bytes and finds no EXIF block).
- **Isolation:** another organization's photo, and another client's photo through the portal, give `404`; the matrices cover every new endpoint.
- **Storage limit** under concurrency (two uploads with room for one).
- **Clean-up:** deleting a job or an organization removes its files through the outbox; the sweep removes an orphan.
- **Checklist rules** and the Completed precondition.
- **Web:** upload states, checklist by keyboard, the portal feedback form.

## Acceptance criteria
- SPEC-V2 criterion 17 met.
- In the browser, at 375 px: complete a visit with a required checklist and a photo; as the contact, see it and confirm it; report a problem on another visit.

## Verification required
Full test suite; the image library decision logged with sources and dates; the production image still builds and starts (native dependencies); browser pass at three widths, keyboard, both themes; the photo viewer opened in the audit; CSP clean.
