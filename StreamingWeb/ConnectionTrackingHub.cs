using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;

namespace StreamingWeb
{
    public class ConnectionTrackingHub : Hub
    {
        private readonly static ConcurrentDictionary<string, CancellationTokenSource>
            ConnectionBrokenTokens = new ConcurrentDictionary<string, CancellationTokenSource>();

        public override Task OnConnected()
        {
            var id = Context.ConnectionId;
            ConnectionBrokenTokens[id] = new CancellationTokenSource();
            return base.OnConnected();
        }
        public override Task OnDisconnected(bool stopCalled)
        {
            var id = Context.ConnectionId;
            CancellationTokenSource cts;
            if (ConnectionBrokenTokens.TryRemove(id, out cts))
                cts.Cancel();
            return base.OnDisconnected(stopCalled);
        }
        public override Task OnReconnected()
        {
            var id = Context.ConnectionId;
            CancellationTokenSource cts;
            if (ConnectionBrokenTokens.TryRemove(id, out cts))
                cts.Cancel();
            ConnectionBrokenTokens[id] = new CancellationTokenSource();
            return base.OnReconnected();
        }

        protected CancellationToken CallerCancellationToken
        {
            get { return ConnectionBrokenTokens[Context.ConnectionId].Token; }
        }
    }
}