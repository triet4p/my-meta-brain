namespace MetaBrain.OwnerUi;

/// <summary>
/// The console's file and folder pickers.
/// <para>
/// The owner names a backup file, a backup file to restore and a folder to
/// restore into, and a file to write the readable export to. Each of those
/// is chosen with a real Windows dialog, so the owner never has to type a
/// path and never has to remember one. The console keeps no path of its own:
/// the chosen path belongs to the form it was chosen in, and it is dropped
/// with the epoch that form belonged to.
/// </para>
/// <para>
/// The dialogs are behind this seam so the console can be driven without a
/// desktop session. The default implementation is the real Windows dialog;
/// nothing else in the product replaces it.
/// </para>
/// </summary>
internal interface IOwnerFilePicker
{
    /// <summary>Asks the owner for the file a new artifact should be written to.</summary>
    string? PickSaveFile(string title, string suggestedFileName);

    /// <summary>Asks the owner for an existing file to read.</summary>
    string? PickOpenFile(string title);

    /// <summary>Asks the owner for an existing folder to use.</summary>
    string? PickFolder(string title);
}

/// <summary>
/// The real Windows file and folder dialogs. The save dialog never asks to
/// overwrite: an existing file is the owner's own file, and this product
/// never replaces one without saying so.
/// </summary>
internal sealed class OwnerSystemFilePicker : IOwnerFilePicker
{
    public string? PickSaveFile(string title, string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            AddExtension = true,
            OverwritePrompt = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
