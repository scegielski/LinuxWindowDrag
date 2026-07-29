using System.Drawing;
using System.Windows.Forms;

namespace LinuxWindowDrag;

internal sealed class DebugForm : Form
{
    private readonly TextBox _debugText;
    private readonly Queue<string> _messageQueue;
    private const int MaxMessages = 1000;

    internal DebugForm()
    {
        _messageQueue = new Queue<string>();

        Text = "Linux Window Drag - Debug";
        Size = new Size(600, 400);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        _debugText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 9),
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
        };
        Controls.Add(_debugText);

        Log("Debug window started");
    }

    internal void Log(string message)
    {
        _messageQueue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        if (_messageQueue.Count > MaxMessages)
        {
            _messageQueue.Dequeue();
        }

        if (InvokeRequired)
        {
            BeginInvoke(UpdateDisplay);
        }
        else
        {
            UpdateDisplay();
        }
    }

    internal void ClearLog()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ClearLogInternal);
        }
        else
        {
            ClearLogInternal();
        }
    }

    private void ClearLogInternal()
    {
        _messageQueue.Clear();
        _debugText.Clear();
    }

    private void UpdateDisplay()
    {
        _debugText.Text = string.Join(Environment.NewLine, _messageQueue);
        _debugText.SelectionStart = _debugText.Text.Length;
        _debugText.ScrollToCaret();
    }
}
