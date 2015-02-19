using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Instrumentation;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using Microsoft.Owin.Cors;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Owin;

// See http://coffeegrammer.com/2015/01/22/using-iobservable-to-make-your-api-signalr-and-rest-ready/
namespace StreamingWeb
{
    /// <summary>
    /// Interface declaration specifying data-exchange model. Will end up JSON serialized.
    /// </summary>
    public interface IData
    {
        int Id { get; }
        string Other { get; }
    }

    /// <summary>
    /// A "DataBase" that can answer queries (slowly and incrementally)
    /// </summary>
    internal class Db
    {
        /// <summary>
        /// The storage model of IData
        /// </summary>
        private class Data : IData
        {
            public int Id { get; set; }
            public string Other { get; set; }
        }

        /// <summary>
        /// Simulate a query that takes a while and returns data in batches
        /// </summary>
        /// <param name="fromId">Start from this id (optional, usefull for resuming after interruption)</param>
        /// <param name="softLimit">Stop processing query after (softly) this many items</param>
        /// <param name="cancellationToken">Abort processing ASAP</param>
        /// <returns>Stream of IData batches</returns>
        public IObservable<IEnumerable<IData>> Query(
            int? fromId = null,
            long? softLimit = 100, 
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Observable.Create<IEnumerable<IData>>(async observer =>
            {
                // Make some items
                var id = fromId ?? 0;
                long count = 0;
                while (!softLimit.HasValue || count < softLimit.Value)
                {
                    // The DB is slow.... will abort immediately if cancellationToken is cancelled
                    await Task.Delay(DelayBetweenEachBatch, cancellationToken);
                    // Got items
                    var batch = Enumerable.Range(0, BatchResultSize)
                        .Select(_ => new Data {Id = id++, Other = "X"});
                    count += BatchResultSize;
                    // Tell that to the observer
                    observer.OnNext(batch);
                }
                // Tell observer no more items will come
                observer.OnCompleted();
            });
        }
        public TimeSpan DelayBetweenEachBatch = TimeSpan.FromSeconds(1);
        public int BatchResultSize = 3;
    }

    /// <summary>
    /// Application responsible for running the server
    /// </summary>
    internal class Program
    {
        internal static Db Db = new Db(); // Use injection instead :)

        private static void Main(string[] args)
        {
            string url = null;
            if (args.Length == 1)
                url = args[0];
            if (url == null)
                url = "http://localhost:8180";
            using (WebApp.Start(url))
            {
                Console.WriteLine("Server running on {0}", url);
                Console.ReadLine();
            }
        }
    }

    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Cross site support
            app.UseCors(CorsOptions.AllowAll);

            // Configure Web API for self-host. 
            var owinConfig = new HttpConfiguration();
            owinConfig.SetupJson();
            owinConfig.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always; // For debugging
            // Web API routes - Allows auto-finding ApiController with RoutePrefix and Route
            owinConfig.MapHttpAttributeRoutes();
            // Disable the "application/xml" media type, so that JSON is returned in e.g. Chrome. For XML, explicitly request media type "text/xml" instead.
            var appXmlType = owinConfig.Formatters.XmlFormatter.SupportedMediaTypes.FirstOrDefault(t => t.MediaType == "application/xml");
            owinConfig.Formatters.XmlFormatter.SupportedMediaTypes.Remove(appXmlType);
            owinConfig.EnsureInitialized();
            app.UseWebApi(owinConfig);

            // Configure for signalR
            var hubConfig = new HubConfiguration {EnableDetailedErrors = true};
            app.MapSignalR(hubConfig);
        }
    }


    [HubName("dataHub")]
    public class DataHub : Hub
    {
        private readonly Db _db;
        public DataHub()
        {
            _db = Program.Db; // Should be injection :)
        }

        public Task Query(int fromId)
        {
            return _query(fromId, null);
        }

        private async Task _query(int? fromId, int? softLimit)
        {
            // send the results as they come out of the query
            await _db.Query(fromId, softLimit, CallerCancellationToken)
                .Do(async batch => await (Task) Clients.Caller.acceptBatch(batch));
        }
        public CancellationToken CallerCancellationToken { get { return CancellationToken.None; }}
    }

    [RoutePrefix("api")]
    public class DataController : ApiController
    {
        private readonly Db _db;
        public DataController()
        {
            _db = Program.Db; // Should be injemoction :)
        }

        [Route("query")]
        [HttpGet]
        public async Task<IEnumerable<IData>> Query([FromUri] int? fromId = null, [FromUri]int? limit = 100)
        {
            var items = _db.Query(fromId, cancellationToken: Request.CallCancelled())
                .SelectMany(x => x); // Concatenate all the batches in a single observable
            if ( limit.HasValue )
                items = items.Take(limit.Value); // We can stop exactly at the softlimit
            var fetch = await items.ToList();
            return fetch;
        }
    }

    public static class ApiControllerExtensions
    {
        public static CancellationToken CallCancelled(this HttpRequestMessage request)
        {
            object token;
            if (request != null 
                && request.GetOwinEnvironment().TryGetValue("owin.CallCancelled", out token) 
                && token is CancellationToken)
                return (CancellationToken) token;
            return CancellationToken.None;
        }
    }

    public static class JsonWebApiSetup
    {
        public static HttpConfiguration SetupJson(this HttpConfiguration config)
        {
            // Configure JSON serializer
            var jsonSettings = config.Formatters.JsonFormatter.SerializerSettings;
            jsonSettings.Formatting = Formatting.Indented;
            jsonSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            jsonSettings.Converters.Add(new StringEnumConverter());
            jsonSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            return config;
        }
    }
}
