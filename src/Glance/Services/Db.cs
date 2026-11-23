using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Glance.Services;

// Fire-and-forget logging to local MongoDB (db "glance"). The app never waits on or fails because of it.
public static class Db
{
    static IMongoDatabase? db;
    static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static void Init(string url)
    {
        try
        {
            var s = MongoClientSettings.FromConnectionString(url);
            s.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            db = new MongoClient(s).GetDatabase("glance");
            var ttl = new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ts"), new CreateIndexOptions { ExpireAfter = Retention });
            Fire(db.GetCollection<BsonDocument>("logs").Indexes.CreateOneAsync(ttl));
            Fire(db.GetCollection<BsonDocument>("metrics").Indexes.CreateOneAsync(ttl));
        }
        catch (Exception e) when (e is MongoConfigurationException or ArgumentException)
        {
            db = null;
            Debug.WriteLine($"mongo: bad url: {e.Message}");
        }
    }

    public static void Info(string msg) => Log("info", msg);
    public static void Error(string msg, Exception e) => Log("error", msg, e);

    static void Log(string level, string msg, Exception? e = null)
    {
        Debug.WriteLine($"{level}: {msg} {e}");
        Insert("logs", new BsonDocument
        {
            { "ts", DateTime.UtcNow }, { "level", level }, { "msg", msg }, { "ex", e?.ToString() ?? (BsonValue)BsonNull.Value },
        });
    }

    /// top: the heaviest apps of the minute, flat ({r: resource, n: app, v: value}) so the history can unwind it in one step.
    public static void Metric(Snapshot s, (double? Disk, Dictionary<string, HardwareMonitor.Use[]> Top) m) => Insert("metrics", new BsonDocument
    {
        { "ts", DateTime.UtcNow },
        { "cpu", s.CpuLoad }, { "cpuTemp", s.CpuTemp }, { "cpuPower", s.CpuPower },
        { "gpu", s.GpuLoad }, { "gpuTemp", s.GpuTemp }, { "gpuPower", s.GpuPower }, { "vramMb", s.VramUsed },
        { "ramGb", s.RamUsed }, { "netDown", s.NetDown }, { "netUp", s.NetUp }, { "disk", m.Disk },
        { "top", new BsonArray(m.Top.SelectMany(r => r.Value.Select(u =>
            new BsonDocument { { "r", r.Key }, { "n", u.Name }, { "v", Math.Round(u.Value, 1) } }))) },
    });

    public static void Warn(string msg) => Log("warn", msg);

    public static readonly string[] MetricFields = ["cpu", "cpuTemp", "gpu", "gpuTemp", "ramGb", "netDown", "netUp", "disk"];

    /// Metrics averaged into fixed time buckets server-side, oldest first. Empty if Mongo isn't configured.
    public static async Task<List<BsonDocument>> History(TimeSpan range, int bucketMinutes)
    {
        if (db is null) return [];
        var group = new BsonDocument("_id", new BsonDocument("$dateTrunc",
            new BsonDocument { { "date", "$ts" }, { "unit", "minute" }, { "binSize", bucketMinutes } }));
        foreach (var f in MetricFields) group.Add(f, new BsonDocument("$avg", "$" + f));
        BsonDocument[] pipeline =
        [
            new("$match", new BsonDocument("ts", new BsonDocument("$gte", DateTime.UtcNow - range))),
            new("$group", group),
            new("$sort", new BsonDocument("_id", 1)),
        ];
        return await db.GetCollection<BsonDocument>("metrics").Aggregate<BsonDocument>(pipeline).ToListAsync();
    }

    /// Per time bucket and resource, the 3 apps that used the most of it on average over the bucket's minutes.
    /// Rows: { _id: { b: bucket start, r: resource }, apps: [{ n, v }] }.
    public static async Task<List<BsonDocument>> TopApps(TimeSpan range, int bucketMinutes)
    {
        if (db is null) return [];
        var bucket = new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$ts" }, { "unit", "minute" }, { "binSize", bucketMinutes } });
        BsonDocument[] pipeline =
        [
            new("$match", new BsonDocument { { "ts", new BsonDocument("$gte", DateTime.UtcNow - range) }, { "top", new BsonDocument("$exists", true) } }),
            // Minutes per bucket first: an app missing from a minute's top used ~0 then, so it averages over all of them.
            new("$group", new BsonDocument { { "_id", bucket }, { "minutes", new BsonDocument("$sum", 1) }, { "top", new BsonDocument("$push", "$top") } }),
            new("$unwind", "$top"),
            new("$unwind", "$top"),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "b", "$_id" }, { "r", "$top.r" }, { "n", "$top.n" } } },
                { "v", new BsonDocument("$sum", "$top.v") }, { "minutes", new BsonDocument("$first", "$minutes") },
            }),
            new("$sort", new BsonDocument("v", -1)),
            new("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "b", "$_id.b" }, { "r", "$_id.r" } } },
                { "apps", new BsonDocument("$push", new BsonDocument { { "n", "$_id.n" }, { "v", new BsonDocument("$divide", new BsonArray { "$v", "$minutes" }) } }) },
            }),
            new("$project", new BsonDocument("apps", new BsonDocument("$slice", new BsonArray { "$apps", 3 }))),
        ];
        return await db.GetCollection<BsonDocument>("metrics").Aggregate<BsonDocument>(pipeline).ToListAsync();
    }

    /// For the Analysis page, over the range: minutes logged; each app's summed use per resource
    /// ({ _id: { n, r }, v }, divide by minutes for its average); and the strained minutes (CPU ≥ cpuLimit %, or
    /// RAM ≥ ramLimitGb), with how often each app was the top user of the strained resource ({ _id: { n, r }, m }).
    public static async Task<BsonDocument?> Analysis(TimeSpan range, double cpuLimit, double ramLimitGb)
    {
        if (db is null) return null;
        // Each resource's apps are stored heaviest first, so the first one of a resource is its top app.
        static BsonDocument Top(string r) => new("$arrayElemAt", new BsonArray
        {
            new BsonDocument("$filter", new BsonDocument { { "input", "$top" }, { "cond", new BsonDocument("$eq", new BsonArray { "$$this.r", r }) } }), 0,
        });
        static BsonDocument If(string field, double limit, string r) => new("$cond", new BsonArray
        {
            new BsonDocument("$gte", new BsonArray { "$" + field, limit }), new BsonArray { Top(r) }, new BsonArray(),
        });
        var strained = new BsonDocument("$match", new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("cpu", new BsonDocument("$gte", cpuLimit)), new BsonDocument("ramGb", new BsonDocument("$gte", ramLimitGb)),
        }));
        BsonDocument[] pipeline =
        [
            new("$match", new BsonDocument { { "ts", new BsonDocument("$gte", DateTime.UtcNow - range) }, { "top", new BsonDocument("$exists", true) } }),
            new("$facet", new BsonDocument
            {
                { "minutes", new BsonArray { new BsonDocument("$count", "n") } },
                { "strained", new BsonArray { strained, new BsonDocument("$count", "n") } },
                { "apps", new BsonArray
                {
                    new BsonDocument("$unwind", "$top"),
                    new BsonDocument("$group", new BsonDocument { { "_id", new BsonDocument { { "n", "$top.n" }, { "r", "$top.r" } } }, { "v", new BsonDocument("$sum", "$top.v") } }),
                } },
                { "culprits", new BsonArray
                {
                    strained,
                    new BsonDocument("$project", new BsonDocument("c", new BsonDocument("$concatArrays", new BsonArray { If("cpu", cpuLimit, "cpu"), If("ramGb", ramLimitGb, "ram") }))),
                    new BsonDocument("$unwind", "$c"),
                    new BsonDocument("$match", new BsonDocument("c.n", new BsonDocument("$type", "string"))), // a resource with no top app that minute
                    new BsonDocument("$group", new BsonDocument { { "_id", new BsonDocument { { "n", "$c.n" }, { "r", "$c.r" } } }, { "m", new BsonDocument("$sum", 1) } }),
                    new BsonDocument("$sort", new BsonDocument("m", -1)),
                    new BsonDocument("$limit", 6),
                } },
            }),
        ];
        return (await db.GetCollection<BsonDocument>("metrics").Aggregate<BsonDocument>(pipeline).ToListAsync()).FirstOrDefault();
    }

    static void Insert(string col, BsonDocument doc)
    {
        if (db is not null) Fire(db.GetCollection<BsonDocument>(col).InsertOneAsync(doc));
    }

    static async void Fire(Task t)
    {
        try { await t; }
        catch (Exception e) { Debug.WriteLine($"mongo: {e.Message}"); } // mongo down: drop the entry, keep running
    }
}
