#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

// Shared between the plugin (.NET Framework 4.6.2) and its tests (.NET 8), so it
// sticks to APIs both have and needs no extra assemblies inside Playnite.
namespace Savepoint.Playnite
{
    /// <summary>The Savepoint agent isn't running (or isn't answering).</summary>
    public class AgentUnavailableException : Exception
    {
        public AgentUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    [DataContract]
    public class AgentRequest
    {
        [DataMember(Name = "cmd")] public string Cmd { get; set; }
        [DataMember(Name = "game_id", EmitDefaultValue = false)] public string GameId { get; set; }
        [DataMember(Name = "type", EmitDefaultValue = false)] public string Type { get; set; }
        [DataMember(Name = "choice", EmitDefaultValue = false)] public string Choice { get; set; }
    }

    [DataContract]
    public class AgentGame
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "exe_names")] public List<string> ExeNames { get; set; }
        [DataMember(Name = "sync_mode")] public string SyncMode { get; set; }
        [DataMember(Name = "running")] public bool Running { get; set; }
        [DataMember(Name = "conflict")] public bool Conflict { get; set; }

        public override string ToString() => Name;
    }

    [DataContract]
    public class AgentResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "outcome")] public string Outcome { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "version_id")] public string VersionId { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "games")] public List<AgentGame> Games { get; set; }
    }

    /// <summary>
    /// Client for the agent's named pipe (DESIGN §6 "IPC"): one JSON object per line each way.
    /// Each call opens its own connection, so calls from different threads don't interfere.
    /// </summary>
    public class AgentClient
    {
        public const string DefaultPipeName = "savepoint";

        private readonly string _pipeName;
        private readonly int _connectTimeoutMs;

        public AgentClient(string pipeName = DefaultPipeName, int connectTimeoutMs = 1500)
        {
            _pipeName = pipeName;
            _connectTimeoutMs = connectTimeoutMs;
        }

        public AgentResponse Send(AgentRequest request, TimeSpan responseTimeout)
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                try
                {
                    pipe.Connect(_connectTimeoutMs);
                }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new AgentUnavailableException("The Savepoint agent isn't running.", ex);
                }

                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                writer.WriteLine(Serialize(request));

                // Pipe streams don't support read timeouts; wait on a task instead and
                // tear the pipe down if the agent takes too long.
                var read = Task.Run(() => reader.ReadLine());
                if (!read.Wait(responseTimeout))
                    throw new AgentUnavailableException("The Savepoint agent didn't answer in time.");
                var line = read.Result;
                if (line == null) throw new AgentUnavailableException("The Savepoint agent closed the connection.");
                return Deserialize<AgentResponse>(line);
            }
            catch (AggregateException ex) when (ex.InnerException is IOException)
            {
                throw new AgentUnavailableException("Lost the connection to the Savepoint agent.", ex.InnerException);
            }
            catch (IOException ex)
            {
                throw new AgentUnavailableException("Lost the connection to the Savepoint agent.", ex);
            }
            finally
            {
                pipe.Dispose();
            }
        }

        public AgentResponse Pull(string gameId, TimeSpan timeout) =>
            Send(new AgentRequest { Cmd = "pull", GameId = gameId }, timeout);

        public AgentResponse Push(string gameId, string type, TimeSpan timeout) =>
            Send(new AgentRequest { Cmd = "push", GameId = gameId, Type = type }, timeout);

        public AgentResponse Restore(string gameId, TimeSpan timeout) =>
            Send(new AgentRequest { Cmd = "restore", GameId = gameId }, timeout);

        /// <param name="choice">"keep_local" or "take_server".</param>
        public AgentResponse Resolve(string gameId, string choice, TimeSpan timeout) =>
            Send(new AgentRequest { Cmd = "resolve", GameId = gameId, Choice = choice }, timeout);

        public List<AgentGame> ListGames(TimeSpan timeout)
        {
            var r = Send(new AgentRequest { Cmd = "list_games" }, timeout);
            if (!r.Ok) throw new AgentUnavailableException(r.Message ?? "list_games failed");
            return r.Games ?? new List<AgentGame>();
        }

        internal static string Serialize<T>(T value)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                s.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        internal static T Deserialize<T>(string json)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)s.ReadObject(ms);
            }
        }
    }
}
