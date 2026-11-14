using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    private string _writeStatus = "", _writeError = "";
    private bool _acceptDirectWriteFeedback;
    private long _directWriteFeedbackRevision;
    private ModbusWriteState _directWriteFeedbackState;

    public string WriteStatus
    {
        get => _writeStatus;
        private set { if (SetProperty(ref _writeStatus, value)) OnPropertyChanged(nameof(WriteStatusVisibility)); }
    }
    public string WriteError
    {
        get => _writeError;
        private set { if (SetProperty(ref _writeError, value)) OnPropertyChanged(nameof(WriteErrorVisibility)); }
    }
    public Visibility WriteStatusVisibility => WriteStatus.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WriteErrorVisibility => WriteError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    private void ClearWriteFeedback()
    {
        _acceptDirectWriteFeedback = false;
        _directWriteFeedbackState = ModbusWriteState.None;
        WriteStatus = WriteError = "";
    }

    private void RejectWriteFeedback(string error)
    {
        ClearWriteFeedback();
        WriteError = error;
    }

    private void AcceptWriteFeedback(long previousRevision)
    {
        _directWriteFeedbackRevision = previousRevision;
        _acceptDirectWriteFeedback = true;
        WriteStatus = WriteStateText(ModbusWriteState.Queued);
    }

    private void RefreshWriteFeedback(ModbusMonitorSnapshot snapshot)
    {
        // Only the admitted direct write owns these fields. General reads,
        // polling and command snapshots must not replace its eventual result.
        if (!_acceptDirectWriteFeedback || !_writeAdmitted || snapshot.Revision <= _directWriteFeedbackRevision) return;
        _directWriteFeedbackRevision = snapshot.Revision;
        if (snapshot.WriteState == ModbusWriteState.None) return;
        _directWriteFeedbackState = snapshot.WriteState;
        WriteStatus = WriteStateText(snapshot.WriteState);
    }

    private void CompleteWriteFeedback(Exception? failure, bool canceled)
    {
        _acceptDirectWriteFeedback = false;
        if (canceled)
            WriteStatus = T("WriteCanceled", "Write stopped · sent values may have taken effect; inspect before retrying");
        else if (failure is not null)
        {
            if (_directWriteFeedbackState != ModbusWriteState.NotVerified)
                WriteStatus = WriteStateText(ModbusWriteState.Failed);
            WriteError = failure.Message;
        }
        else if (_directWriteFeedbackState is not (ModbusWriteState.Verified or ModbusWriteState.NotVerified or ModbusWriteState.Failed))
            WriteStatus = WriteStateText(ModbusWriteState.NotVerified);
    }

    private static string WriteStateText(ModbusWriteState state) => state switch
    {
        ModbusWriteState.Queued => T("WriteQueued", "Write queued"),
        ModbusWriteState.Writing => T("Writing", "Writing"),
        ModbusWriteState.Verified => T("WriteVerified", "Write verified"),
        ModbusWriteState.NotVerified => T("WriteNotVerified", "Write not verified; inspect before retrying"),
        _ => T("WriteFailed", "Write failed; inspect before retrying")
    };
}
