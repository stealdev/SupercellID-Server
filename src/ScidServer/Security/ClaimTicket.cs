namespace ScidServer.Security;

public sealed record ClaimTicket(string Scid, string Pid, string ItemId, string ProductId);
