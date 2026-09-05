using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>UI orchestration for asynchronous exports, result adoption and close protection.</summary>
public sealed partial class EditorWindow
{
    private bool _fileBusy;
    private bool _confirmingClose;
    private bool _closeAfterSave;
    private Action? _afterCloseSave;
    internal string FilePath => _files.Path;
    internal string? PendingSavePath { get; private set; }
    internal Func<string, bool>? CanSaveTo { get; set; }

    /// <summary>Writes the flattened image over the original. A crop frame the user is still
    /// dragging is applied too: the footer says "Save to apply", so Save applies it.</summary>
    private void Save() => StartFileAction(saveAs: false, copy: false);

    private void RunFileAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Error("editor.file_action", exception, _files.Path);
            ShowToast("Could not complete action. Check the file or clipboard and retry.");
        }
    }

    /// <summary>Writes the flattened image somewhere new and continues editing it there.</summary>
    private void SaveAs() => StartFileAction(saveAs: true, copy: false);

    private void CopyToClipboard() => StartFileAction(saveAs: false, copy: true);

    private void StartFileAction(bool saveAs, bool copy)
    {
        if (_image is null || _fileBusy) return;
        _fileBusy = true;
        try
        {
            var request = saveAs
                ? _files.PrepareSaveAs((name, folder) => FilePicker.SavePng(Handle, name, folder))
                : _files.PrepareSave();
            if (request is null) { _fileBusy = false; _closeAfterSave = false; return; }
            if (!copy && CanSaveTo?.Invoke(request.Destination) == false)
                throw new InvalidOperationException("That file is already open in another editor. Choose a different name.");
            if (!copy) PendingSavePath = request.Destination;
            ShowToast(copy ? "Copying…" : "Saving…");
            _ = ExecuteFileAction(request, saveAs, copy);
        }
        catch
        {
            _fileBusy = false;
            _closeAfterSave = false;
            _afterCloseSave = null;
            PendingSavePath = null;
            throw;
        }
    }

    private async Task ExecuteFileAction(ExportRequest request, bool saveAs, bool copy)
    {
        Exception? failure = null;
        DecodedImage? savedPixels = null;
        var saved = false;
        try
        {
            savedPixels = await MediaWorker.Run(() =>
            {
                if (copy) { request.CopyToClipboard(); return null; }
                request.Save();
                saved = true;
                return ImageSurface.Decode(request.Destination);
            });
        }
        catch (Exception exception) { failure = exception; }
        _dispatch.Post(() =>
        {
            using var completedPixels = savedPixels;
            _fileBusy = false;
            PendingSavePath = null;
            if (failure is not null)
            {
                _closeAfterSave = false;
                _afterCloseSave = null;
                Log.Error("editor.file_action", failure, _files.Path);
                if (saved)
                {
                    _files.CompleteSave(request);
                    _effects?.Dispose();
                    _effects = null;
                    _image?.Dispose();
                    _image = null;
                    _loadError = "Saved successfully. Reopen the image to reload its preview.";
                    RunFileAction(() =>
                    {
                        if (saveAs) SavedAs?.Invoke(_files.Path);
                        else Saved?.Invoke(_files.Path);
                    });
                    Invalidate();
                    return;
                }
                ShowToast("Could not complete action. Check the file or clipboard and retry.");
                return;
            }
            if (copy) _copied.Start(Environment.TickCount64);
            else
            {
                _files.CompleteSave(request);
                RunFileAction(() =>
                {
                    if (saveAs) SavedAs?.Invoke(_files.Path);
                    else Saved?.Invoke(_files.Path);
                    try { ReloadImage(savedPixels!); }
                    catch
                    {
                        _effects?.Dispose();
                        _effects = null;
                        _image?.Dispose();
                        _image = null;
                        _loadError = "Saved successfully. Reopen the image to reload its preview.";
                        throw;
                    }
                    ShowToast("Saved");
                });
            }
            Invalidate();
            if (_closeAfterSave)
            {
                _closeAfterSave = false;
                var continuation = _afterCloseSave;
                _afterCloseSave = null;
                Close();
                continuation?.Invoke();
            }
        }, () => savedPixels?.Dispose());
    }

    internal bool RequestClose(Action? afterSave = null)
    {
        if (_fileBusy || _confirmingClose) return false;
        CommitText();
        if (!_document.HasUnsavedChanges) return true;
        _confirmingClose = true;
        int choice;
        try { choice = UserFeedback.ConfirmSave(Handle, _files.FileName); }
        finally { _confirmingClose = false; }
        if (choice == 7) return true;
        if (choice == 6)
        {
            _closeAfterSave = true;
            _afterCloseSave = afterSave;
            RunFileAction(Save);
        }
        return false;
    }

    /// <summary>A brief confirmation in the footer, so an action that changes nothing visible still
    /// says it happened.</summary>
    private void ShowToast(string message)
    {
        _toast = message;
        _toastUntil = DateTime.UtcNow.AddSeconds(2);
        Invalidate();
    }

    private string? _toast;
    private DateTime _toastUntil;
    private readonly ConfirmFeedback _copied = new();

    /// <summary>Uploads the saved pixels decoded by the worker, then swaps GPU resources.</summary>
    private void ReloadImage(DecodedImage pixels)
    {
        if (_resources is null || RenderTarget is null) return;
        using var target = RenderTarget.AsRenderTarget();
        using var context = target.AsDeviceContext();
        if (context is null) return;

        var image = ImageSurface.Upload(pixels, context);
        PixelEffectSource effects;
        try { effects = new PixelEffectSource(image, _resources); }
        catch { image.Dispose(); throw; }
        _effects?.Dispose();
        _image?.Dispose();
        _image = image;
        _effects = effects;
        _document.SetImageSize(_image.Width, _image.Height);
        Invalidate();
    }
}
