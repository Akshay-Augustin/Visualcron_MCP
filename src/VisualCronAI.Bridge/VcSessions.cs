using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualCron;
using VisualCronAPI;
using VisualCronAI.Contracts;

namespace VisualCronAI.Bridge
{
    /// <summary>Holds one VisualCron server connection per logical environment (DEV/UAT/PROD).</summary>
    internal sealed class VcSessions : IDisposable
    {
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private Client? _client;

        private sealed class Session
        {
            public Server Server = null!;
            public ConnectParams Params = null!;
        }

        public static string ApiVersion() => typeof(Client).Assembly.GetName().Version?.ToString() ?? "";

        public ServerInfoDto Connect(ConnectParams p)
        {
            Validate(p);
            if (_sessions.TryGetValue(p.Environment, out var existing))
            {
                bool same = string.Equals(existing.Params.Address, p.Address, StringComparison.OrdinalIgnoreCase) && existing.Params.Port == p.Port;
                if (same && SafeConnected(existing.Server)) return Info(p.Environment, existing);
                _sessions.Remove(p.Environment);
                if (!_sessions.Values.Any(s => ReferenceEquals(s.Server, existing.Server))) SafeDisconnect(existing.Server);
            }

            // Several environments (DEV/UAT/PROD job groups) may live on one server: share a single connection.
            foreach (var other in _sessions.Values)
            {
                if (string.Equals(other.Params.Address, p.Address, StringComparison.OrdinalIgnoreCase) && other.Params.Port == p.Port
                    && string.Equals(other.Params.AuthMode, p.AuthMode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(other.Params.CredentialTarget ?? "", p.CredentialTarget ?? "", StringComparison.Ordinal)
                    && SafeConnected(other.Server))
                {
                    var shared = new Session { Server = other.Server, Params = p };
                    _sessions[p.Environment] = shared;
                    BridgeLog.Info("Environment " + p.Environment + " shares the connection to " + p.Address + ":" + p.Port);
                    return Info(p.Environment, shared);
                }
            }

            if (_client == null) _client = new Client();

            var conn = new Connection
            {
                ConnectionType = Connection.ConnectionT.Remote,
                Address = p.Address,
                Port = p.Port,
                ClientType = ClientConnectionClass.ClientT.APIClient,
                TimeOut = p.TimeoutSeconds,
            };

            if (string.Equals(p.AuthMode, AuthModes.ActiveDirectory, StringComparison.OrdinalIgnoreCase))
            {
                conn.UseADLogon = true;
                conn.AuthType = LoginClass.AuthT.AD;
            }
            else if (string.Equals(p.AuthMode, AuthModes.CredentialManager, StringComparison.OrdinalIgnoreCase))
            {
                // The password is read here, inside the bridge process, and handed straight to the vendor API.
                // It is never returned, logged or sent to the MCP host.
                var cred = CredentialStore.ReadGeneric(p.CredentialTarget ?? "");
                conn.UseADLogon = false;
                conn.AuthType = LoginClass.AuthT.Password;
                conn.UserName = cred.UserName;
                conn.PassWord = cred.Password;
            }
            else
            {
                throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Unsupported auth mode: " + p.AuthMode);
            }

            BridgeLog.Info("Connecting to environment " + p.Environment + " at " + p.Address + ":" + p.Port + " (auth=" + p.AuthMode + ")");
            Server? server;
            try
            {
                var connectTask = Task.Run(() => _client.Connect(conn, true, false));
                if (!connectTask.Wait(TimeSpan.FromSeconds(p.TimeoutSeconds + 5)))
                    throw new BridgeException(BridgeErrorCodes.ConnectionFailed, "Timed out connecting to " + p.Address + ":" + p.Port + ".");
                server = connectTask.Result;
            }
            catch (AggregateException ae) when (ae.GetBaseException() is Client.ClientLoginFailedException)
            {
                throw new BridgeException(BridgeErrorCodes.ConnectionFailed, "VisualCron login failed for environment " + p.Environment + " (check the account's VisualCron permissions / credential).");
            }
            catch (AggregateException ae)
            {
                var inner = ae.GetBaseException();
                throw new BridgeException(BridgeErrorCodes.ConnectionFailed, "Could not connect to " + p.Address + ":" + p.Port + ": " + inner.GetType().Name + ": " + Clean(inner.Message));
            }
            finally
            {
                conn.PassWord = "";
            }

            if (server == null || !SafeConnected(server))
                throw new BridgeException(BridgeErrorCodes.ConnectionFailed, "VisualCron server did not accept the connection: " + p.Address + ":" + p.Port);

            WaitForSync(server, TimeSpan.FromSeconds(Math.Max(10, p.TimeoutSeconds)));
            bool authenticated;
            try { authenticated = server.Authenticated; } catch { authenticated = false; }
            if (!authenticated)
            {
                SafeDisconnect(server);
                throw new BridgeException(BridgeErrorCodes.ConnectionFailed,
                    "Reached " + p.Address + ":" + p.Port + " but VisualCron did not authenticate the session (auth=" + p.AuthMode + "). " +
                    (string.Equals(p.AuthMode, AuthModes.ActiveDirectory, StringComparison.OrdinalIgnoreCase)
                        ? "AD logon is probably not enabled for this Windows account on the server; use AuthMode=CredentialManager with a VisualCron user."
                        : "Check the VisualCron user name/password stored in Credential Manager target '" + p.CredentialTarget + "'."));
            }
            var session = new Session { Server = server, Params = p };
            _sessions[p.Environment] = session;
            BridgeLog.Info("Connected to " + p.Environment + " (server version " + server.Version + ")");
            return Info(p.Environment, session);
        }

        public ServerInfoDto Test(string environment)
        {
            var session = GetSession(environment);
            bool ok;
            try { ok = session.Server.TestConnection(); }
            catch (Exception ex) { throw new BridgeException(BridgeErrorCodes.NotConnected, "Connection test failed: " + Clean(ex.GetBaseException().Message)); }
            var info = Info(environment, session);
            info.Connected = ok && info.Connected;
            return info;
        }

        public Server Get(string environment) => GetSession(environment).Server;

        public bool IsConnected(string environment) =>
            _sessions.TryGetValue(environment ?? "", out var s) && SafeConnected(s.Server);

        private Session GetSession(string environment)
        {
            if (string.IsNullOrWhiteSpace(environment) || !_sessions.TryGetValue(environment, out var session))
                throw new BridgeException(BridgeErrorCodes.NotConnected, "Not connected to environment '" + environment + "'.");
            if (!SafeConnected(session.Server))
                throw new BridgeException(BridgeErrorCodes.NotConnected, "Connection to environment '" + environment + "' was lost.");
            return session;
        }

        private static ServerInfoDto Info(string env, Session s)
        {
            var server = s.Server;
            var info = new ServerInfoDto
            {
                Environment = env,
                Address = s.Params.Address,
                Port = s.Params.Port,
                AuthMode = s.Params.AuthMode,
                Connected = SafeConnected(server),
            };
            try { info.ServerVersion = server.Version ?? ""; } catch { }
            try { info.TimeZoneName = server.TimeZoneName ?? ""; } catch { }
            try { info.UtcOffsetMinutes = (int)server.UTCTimeOffset.TotalMinutes; } catch { }
            try { info.ServerTime = server.ServerTime; } catch { }
            try { info.JobCount = server.Jobs.Count(); } catch { }
            return info;
        }

        private static void WaitForSync(Server server, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                bool inSync;
                try { inSync = server.InSync; } catch { return; }
                if (inSync) return;
                Thread.Sleep(200);
            }
            BridgeLog.Warn("Server object synchronization did not report completion within " + timeout.TotalSeconds + "s; continuing.");
        }

        /// <summary>VisualCron embeds stack traces in some messages ("... (   at ..."); keep only the human part.</summary>
        internal static string Clean(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int i = message.IndexOf("(   at ", StringComparison.Ordinal);
            if (i < 0) i = message.IndexOf("\n   at ", StringComparison.Ordinal);
            return (i > 0 ? message.Substring(0, i) : message).Trim();
        }

        private static bool SafeConnected(Server s)
        {
            try { return s.Connected; } catch { return false; }
        }

        private static void SafeDisconnect(Server s)
        {
            try { s.Disconnect(); } catch { }
        }

        private static void Validate(ConnectParams p)
        {
            if (string.IsNullOrWhiteSpace(p.Environment)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Environment is required.");
            if (string.IsNullOrWhiteSpace(p.Address)) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Address is required.");
            if (p.Port <= 0 || p.Port > 65535) throw new BridgeException(BridgeErrorCodes.InvalidRequest, "Port is out of range.");
            if (p.TimeoutSeconds <= 0 || p.TimeoutSeconds > 300) p.TimeoutSeconds = 30;
        }

        public void Dispose()
        {
            foreach (var server in _sessions.Values.Select(s => s.Server).Distinct()) SafeDisconnect(server);
            _sessions.Clear();
            try { _client?.DisconnectAll(); } catch { }
            try { _client?.Dispose(); } catch { }
        }
    }
}
