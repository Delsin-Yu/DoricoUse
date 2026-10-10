using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Keeps the current Dorico status up to date.
/// </summary>
/// <remarks>
/// Dorico sends the full status in answer to getstatus, but the unsolicited status messages it pushes while the
/// user works are partial patches. A patch is merged into the last known status; a null in a patch never clears
/// an existing value.
/// </remarks>
internal sealed class StatusTracker
{
    private readonly Lock _gate = new();

    private JsonObject? _current;

    public JsonObject? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is null ? null : (JsonObject)_current.DeepClone();
            }
        }
    }

    public int Revision { get; private set; }

    /// <summary>
    /// Merges a status message into the last known status.
    /// </summary>
    /// <remarks>
    /// Dorico answers getstatus with a complete status and pushes partial patches the rest of the time — but an
    /// answer sent while a modal dialog is up (loading a project, for instance) is partial as well, so every
    /// status is merged the same way instead of replacing what is already known.
    /// </remarks>
    public void Apply(JsonObject status)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                _current = (JsonObject)status.DeepClone();
            }
            else
            {
                Merge(_current, status);
            }

            Revision++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                continue;
            }

            if (value is JsonObject nested && target[key] is JsonObject existing)
            {
                Merge(existing, nested);
                continue;
            }

            target[key] = value.DeepClone();
        }
    }
}
