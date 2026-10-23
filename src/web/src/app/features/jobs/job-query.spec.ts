import { activeFilterCount, jobApiParams, jobUrlChanges, parseJobQuery } from './job-query';

describe('job query (URL ↔ API)', () => {
  it('reads every filter from the URL, keeping known values in a fixed order', () => {
    const query = parseJobQuery({
      q: '  JOB-0042 ',
      status: 'completed,new,bogus,new',
      priority: 'low,urgent',
      assignee: 'me',
      clientId: '0199f0a0-0000-7000-8000-0000000000c1',
      overdue: 'true',
      dueFrom: '2026-10-01',
      dueTo: '2026-10-31',
      sort: 'due',
      page: '3',
    });

    expect(query).toEqual({
      q: 'JOB-0042',
      statuses: ['new', 'completed'],
      priorities: ['urgent', 'low'],
      assignee: 'me',
      clientId: '0199f0a0-0000-7000-8000-0000000000c1',
      overdue: true,
      dueFrom: '2026-10-01',
      dueTo: '2026-10-31',
      sort: 'due',
      page: 3,
    });
    expect(activeFilterCount(query)).toBe(6);
  });

  it('falls back to the defaults for anything it does not recognize', () => {
    const query = parseJobQuery({
      assignee: 'someone',
      clientId: 'not-a-guid',
      overdue: 'yes',
      dueFrom: '01/10/2026',
      sort: 'salary',
      page: '-2',
    });

    expect(query).toEqual({
      q: '',
      statuses: [],
      priorities: [],
      assignee: '',
      clientId: '',
      overdue: false,
      dueFrom: '',
      dueTo: '',
      sort: '-number',
      page: 1,
    });
    expect(activeFilterCount(query)).toBe(0);
  });

  it('sends multi-value filters to the API as repeated parameters and omits empty ones', () => {
    const query = parseJobQuery({ status: 'new,scheduled', assignee: 'none', overdue: 'true' });

    expect(jobApiParams(query, 25)).toEqual({
      sort: '-number',
      page: '1',
      pageSize: '25',
      status: ['new', 'scheduled'],
      assignee: 'none',
      overdue: 'true',
    });
  });

  it('writes changes back to the URL comma-joined, with defaults removed so links stay short', () => {
    expect(
      jobUrlChanges({
        statuses: ['new', 'inProgress'],
        priorities: [],
        overdue: false,
        sort: '-number',
        page: 1,
        assignee: 'me',
        q: '',
      }),
    ).toEqual({
      status: 'new,inProgress',
      priority: null,
      overdue: null,
      sort: null,
      page: null,
      assignee: 'me',
      q: null,
    });
    expect(jobUrlChanges({ overdue: true, sort: 'due', page: 2 })).toEqual({
      overdue: 'true',
      sort: 'due',
      page: '2',
    });
  });

  it('round-trips: what the URL gets back parses to the same query', () => {
    const changes = jobUrlChanges({
      statuses: ['scheduled', 'cancelled'],
      priorities: ['high'],
      overdue: true,
      sort: '-priority',
      page: 4,
    });
    const raw = Object.fromEntries(Object.entries(changes).filter(([, v]) => v !== null)) as Record<
      string,
      string
    >;

    const query = parseJobQuery(raw);

    expect(query.statuses).toEqual(['scheduled', 'cancelled']);
    expect(query.priorities).toEqual(['high']);
    expect([query.overdue, query.sort, query.page]).toEqual([true, '-priority', 4]);
  });
});
