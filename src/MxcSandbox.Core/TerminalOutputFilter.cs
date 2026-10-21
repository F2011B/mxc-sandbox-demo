namespace MxcSandbox.Core;

/// <summary>Keep terminal drawing/CSI sequences, strip OSC/DCS/APC/PM/SOS control
/// strings (including clipboard queries and terminal passthrough). State survives chunks.</summary>
public sealed class TerminalOutputFilter
{
    private enum Mode { Text, Escape, String, StringEscape }
    private Mode mode;
    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        foreach (var value in input)
        {
            switch (mode)
            {
                case Mode.Text:
                    if (value == 27) mode = Mode.Escape; else output.Add(value);
                    break;
                case Mode.Escape:
                    if (value == 27) break; // Restarted escape must not smuggle a control string.
                    if (value is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X') mode = Mode.String;
                    else { output.Add(27); output.Add(value); mode = Mode.Text; }
                    break;
                case Mode.String:
                    if (value == 7) mode = Mode.Text;
                    else if (value == 27) mode = Mode.StringEscape;
                    break;
                case Mode.StringEscape:
                    if (value == (byte)'\\') mode = Mode.Text;
                    else if (value != 27) mode = Mode.String;
                    break;
            }
        }
        return output.ToArray();
    }
}
