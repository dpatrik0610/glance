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

    public static void Metric(Snapshot s) => Insert("metrics", new BsonDocument
    {
        { "ts", DateTime.UtcNow },
        { "cpu", s.CpuLoad }, { "cpuTemp", s.CpuTemp }, { "cpuPower", s.CpuPower },
        { "gpu", s.GpuLoad }, { "gpuTemp", s.GpuTemp }, { "gpuPower", s.GpuPower }, { "vramMb", s.VramUsed },
        { "ramGb", s.RamUsed }, { "netDown", s.NetDown }, { "netUp", s.NetUp },
    });

    public static void Warn(string msg) => Log("warn", msg);

    public static readonly string[] MetricFields = ["cpu", "cpuTemp", "gpu", "gpuTemp", "ramGb", "netDown"];

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
