
using EventStore.Logging;


namespace EventStorePerformance
{

    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using EventStore;
    using EventStore.Persistence.MongoPersistence;
    using EventStore.Persistence.SqlPersistence.SqlDialects;
    using EventStore.Serialization;

    public class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |",
                "DB".PadRight(18),
                "Run #",
                "Stream Count",
                "Events/Stream",
                "Total Events",
                "Insert Time (s)",
                "Insert Rate (events/s)",
                "Read Rate (events/s)");
            int runs = 3;
            //new SqlServerPerfTest().Run(runs);
            //new PostgreSqlPerfTest().Run(runs);
            //new MySqlPerfTest().Run(runs);
            //new RavenPerfTest().Run(runs);
            new MongoPerfTest().Run(runs);
            Console.WriteLine("Done. Press any key.");
            Console.ReadLine();
        }
    }

    public abstract class PerfTestBase
    {
        public void Run(int runs)
        {
            for (int n = 0; n < runs; n++)
            {
                Wireup wireup = Wireup.Init();
                PersistenceWireup persistenceWireup = ConfigurePersistence(wireup);
                using (IStoreEvents storeEvents = persistenceWireup.InitializeStorageEngine().UsingBinarySerialization().Build())
                {
                    storeEvents.Advanced.Purge();

                    // 1: Insert Test.
                    // Follows typical real world usage - parallel inserts one event per stream
                    int streamCount = 100;
                    int eventsPerStream = 100;
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();

                    Parallel.For(0, streamCount, i =>
                    {
                        InsertEventsIntoStream(storeEvents, eventsPerStream);
                    });
                    stopwatch.Stop();
                    int totalEvents = streamCount * eventsPerStream;
                    double insertRate = Math.Round(totalEvents / stopwatch.Elapsed.TotalSeconds, 0);

                    // 2. Read all events
                    // Interest to see how quickly one can replay *all* events in order to rebuild projections
                    var timings = new List<double>();
                    var readStopwatch = new Stopwatch();
                    readStopwatch.Start();
                    foreach (var @event in storeEvents.Advanced.GetFrom(DateTime.MinValue).SelectMany(c => c.Events).Select(m => (Event)(m.Body)))
                    {
                        timings.Add(Math.Round(readStopwatch.Elapsed.TotalSeconds, 7));
                        readStopwatch.Restart();
                    }
                    // GC can create anomolies in the timings, so discard them.
                    timings = timings.RemoveOutliers();
                    double readRate = Math.Round(timings.Count() / timings.Sum(), 0);

                    Console.WriteLine("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |",
                        GetType().Name.PadRight(18),
                        n.ToString().PadRight(5),
                        streamCount.ToString().PadRight(12),
                        eventsPerStream.ToString().PadRight(13),
                        totalEvents.ToString().PadRight(12),
                        stopwatch.Elapsed.TotalSeconds.ToString().PadRight(15),
                        insertRate.ToString().PadRight(22),
                        readRate.ToString().PadRight(20));
                    storeEvents.Dispose();
                }
            }
        }

        private void InsertEventsIntoStream(IStoreEvents storeEvents, int eventsPerStream)
        {
            Guid streamId = Guid.NewGuid();
            for (int j = 0; j < eventsPerStream; j++)
            {
                using (IEventStream eventStream = storeEvents.OpenStream(streamId, 0, int.MaxValue))
                {
                    eventStream.Add(new EventMessage { Body = CreateEvent() });
                    eventStream.CommitChanges(Guid.NewGuid());
                }
            }
        }

        protected abstract PersistenceWireup ConfigurePersistence(Wireup wireup);

        private Event CreateEvent()
        {
            return new Event
            {
                Id = Guid.NewGuid(),
                Address = RandomString(50),
                Name = RandomString(20),
                DateOfBirth = new DateTime(1980, 1, 1)
            };
        }

        private static readonly Random Random = new Random();
        private string RandomString(int size)
        {
            var builder = new StringBuilder();
            char ch;
            for (int i = 0; i < size; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * Random.NextDouble() + 65)));
                builder.Append(ch);
            }

            return builder.ToString();
        }

        [Serializable]
        public class Event
        {
            public Guid Id { get; set; }

            public string Name { get; set; }

            public string Address { get; set; }

            public DateTime DateOfBirth { get; set; }
        }
    }

    public class SqlServerPerfTest : PerfTestBase
    {
        protected override PersistenceWireup ConfigurePersistence(Wireup wireup)
        {
            return wireup
                .UsingSqlPersistence("SqlServer")
                .WithDialect(new MsSqlDialect());
        }
    }

    public class PostgreSqlPerfTest : PerfTestBase
    {
        protected override PersistenceWireup ConfigurePersistence(Wireup wireup)
        {
            return wireup
                .UsingSqlPersistence("PostgreSql")
                .WithDialect(new PostgreSqlDialect());
        }
    }

    public class MySqlPerfTest : PerfTestBase
    {
        protected override PersistenceWireup ConfigurePersistence(Wireup wireup)
        {
            return wireup
                .UsingSqlPersistence("MySql")
                .WithDialect(new MySqlDialect());
        }
    }

    public class RavenPerfTest : PerfTestBase
    {
        protected override PersistenceWireup ConfigurePersistence(Wireup wireup)
        {
            return wireup.UsingRavenPersistence("Raven");
        }
    }

    public class MongoPerfTest : PerfTestBase
    {
        protected override PersistenceWireup ConfigurePersistence(Wireup wireup)
        {
            return wireup.UsingMongoPersistence("Mongo", new DocumentObjectSerializer());
        }
    }

    public class MongoPersistenceFactoryFromConnectionString : MongoPersistenceFactory
    {
        private readonly string _connectionString;
        public MongoPersistenceFactoryFromConnectionString(string connectionString, IDocumentSerializer serializer)
            : base(connectionString, serializer)
        {
            _connectionString = connectionString;
        }

        protected override string GetConnectionString()
        {
            return _connectionString;
        }
    }


    public class MongoPersistenceWireupFromConnectionString : PersistenceWireup
    {
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof(MongoPersistenceWireupFromConnectionString));

        public MongoPersistenceWireupFromConnectionString(Wireup inner, string connectionString, IDocumentSerializer serializer)
            : base(inner)
        {
            Logger.Debug("Configuring Mongo persistence engine.");
            Container.Register(c => new MongoPersistenceFactoryFromConnectionString(connectionString, serializer).Build());
        }
    }

    public static class MongoPersistenceWireupExtensions
    {
        public static PersistenceWireup UsingMongoPersistenceFromConnectionString(
            this Wireup wireup, string connectionName, IDocumentSerializer serializer)
        {
            return new MongoPersistenceWireupFromConnectionString(wireup, connectionName, serializer);
        }

    }

    public static class MathExtenions
    {
        public static List<double> RemoveOutliers(this List<double> instance)
        {
            instance.Sort();
            int lqIndex = Convert.ToInt32(Math.Round(instance.Count * 0.25));
            int uqIndex = Convert.ToInt32(Math.Round(instance.Count * 0.75));
            double innerQuartileRange = instance[uqIndex] - instance[lqIndex];
            double lowerSuspectOutlierValue = instance[uqIndex] - (1.5 * innerQuartileRange);
            double upperSuspectOutlierValue = instance[lqIndex] + (1.5 * innerQuartileRange);
            return instance.Where(s => s > lowerSuspectOutlierValue && s < upperSuspectOutlierValue).ToList();
        }
    }

}

