namespace Resesh.App.Interop;

/// <summary>
/// Drive letters handed to SSHFS mounts, shared by every tab in the process. Two tabs
/// mounting at once both saw the same letter free (the first mount had not appeared yet),
/// and the second then took the first's drive for its own. A letter stays reserved from
/// the moment it is chosen until its mount fails or is disposed.
/// </summary>
internal sealed class DriveLetterReservations
{
    public static DriveLetterReservations Shared { get; } = new();

    private readonly Lock _gate = new();
    private readonly HashSet<char> _reserved = [];

    /// <summary>Reserves the highest letter from Z down to D that is neither in
    /// <paramref name="inUse"/> nor already reserved; null when none is left.</summary>
    public char? Reserve(IEnumerable<char> inUse)
    {
        var used = inUse.Select(char.ToUpperInvariant).ToHashSet();
        lock (_gate)
        {
            for (var letter = 'Z'; letter >= 'D'; letter--)
            {
                if (!used.Contains(letter) && _reserved.Add(letter))
                    return letter;
            }
        }
        return null;
    }

    public void Release(char letter)
    {
        lock (_gate)
            _reserved.Remove(char.ToUpperInvariant(letter));
    }

    public bool IsReserved(char letter)
    {
        lock (_gate)
            return _reserved.Contains(char.ToUpperInvariant(letter));
    }
}
