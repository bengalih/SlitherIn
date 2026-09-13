using System;
using System.Collections.Generic;
using System.Linq;
using SlitherIn.Core.Config;
using SlitherIn.Input;

namespace SlitherIn.Tests
{
    /// <summary>In-memory IKeyEngine that records every SetState/ReleaseAll into a
    /// string log: canonical chord names joined by '+', or "&lt;up&gt;" for an empty
    /// set, or "release-all". Lets dispatcher tests assert injection exactly.</summary>
    internal sealed class RecordingEngine : IKeyEngine
    {
        public List<string> Log { get; } = new();

        public bool Available => true;
        public string Name => "recorder";

        public void Initialize() { }

        public void SetState(IReadOnlyList<Chord> pressed) =>
            Log.Add(pressed.Count == 0 ? "<up>" : string.Join("+", pressed.Select(c => c.ToString())));

        public void ReleaseAll() => Log.Add("release-all");

        public void Dispose() { }
    }
}