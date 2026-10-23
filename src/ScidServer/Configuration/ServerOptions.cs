namespace ScidServer.Configuration;

public sealed class ServerOptions
{
    public string KeyPath { get; init; } = "scid-server-key.pem";
    public string FixedPin { get; init; } = "";
    public string UniversalLinkBase { get; init; } = "https://link.example.com";
    public string CdnUpstream { get; init; } = "https://cdn.id.supercell.com/assets/";
    public bool EnableDevEndpoints { get; init; } = true;
    public bool LogBodies { get; init; } = true;
}
