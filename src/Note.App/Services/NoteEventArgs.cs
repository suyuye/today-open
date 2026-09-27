using NoteApp.Models;

namespace NoteApp.Services;

/// <summary>便签事件参数。</summary>
public class NoteEventArgs : EventArgs
{
    public NoteEventArgs(Note note)
    {
        Note = note;
    }

    public Note Note { get; }
}
