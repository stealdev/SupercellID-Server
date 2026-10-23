namespace ScidServer.Security;

public sealed record Identity(string Scid, string Pid, long GameAccountId, string Email, string Name, int Version);
