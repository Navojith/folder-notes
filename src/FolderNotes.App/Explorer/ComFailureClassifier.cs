namespace FolderNotes.App.Explorer;

internal enum ComFailureClass
{
    /// <summary>Not a failure (<c>S_OK</c>, <c>S_FALSE</c>, any non-negative HRESULT).</summary>
    None,

    /// <summary>Keep the entry and retry on the ladder.</summary>
    Transient,

    /// <summary>The object (or its window) is gone: drop the entry; from the root it means recovery.</summary>
    Gone,

    /// <summary>Unexpected: path is unknown this time; drop the entry after repeated failures.</summary>
    Other,
}

/// <summary>HRESULT → action table of 04 § ExplorerTracker design B.5. Pure; no COM.</summary>
internal static class ComFailureClassifier
{
    public static ComFailureClass Classify(int hr, bool windowAlive = true)
    {
        if (hr >= 0)
        {
            return ComFailureClass.None;
        }

        switch (unchecked((uint)hr))
        {
            case 0x80010001: // RPC_E_CALL_REJECTED
            case 0x8001010A: // RPC_E_SERVERCALL_RETRYLATER
            case 0x80010002: // RPC_E_CALL_CANCELED
                return ComFailureClass.Transient;

            case 0x80010108: // RPC_E_DISCONNECTED
            case 0x800401FD: // CO_E_OBJNOTCONNECTED
            case 0x80010007: // RPC_E_SERVER_DIED
            case 0x80010012: // RPC_E_SERVER_DIED_DNE
            case 0x800706BA: // RPC_S_SERVER_UNAVAILABLE
            case 0x800706BE: // RPC_S_CALL_FAILED
            case 0x80010114: // RPC_E_INVALID_OBJECT
                return ComFailureClass.Gone;

            case 0x80004005 when !windowAlive: // E_FAIL from a closed window's objects (S5/P1)
                return ComFailureClass.Gone;

            default: // E_FAIL, E_NOINTERFACE, E_NOTIMPL, E_UNEXPECTED and anything unknown
                return ComFailureClass.Other;
        }
    }
}
