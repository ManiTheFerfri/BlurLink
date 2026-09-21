namespace BlurLink.Shell.ViewModels;

public static class RefusalExplainer
{
    public static string Explain(string code) => code switch
    {
        "dedup" => "re-captured echoes suppressed (loop backstop)",
        "rate" => "bursts refused by the rate limiter",
        "payload-gate" => "packets failing the payload prefix gate",
        "fragments" => "IP fragments reinjected, never cloned",
        "host-broadcast" => "broadcast-shaped replies refused (shape unverified)",
        "host-ambiguous" => "replies matching two players, refused not misdelivered",
        "host-unmatched" => "replies to unknown address/port",
        "host-collision" => "players refused on address+port collision",
        "announce-rejected" => "malformed introductions ignored",
        "injection-error" => "replies that could not be sent",
        _ => "unknown refusal",
    };
}
