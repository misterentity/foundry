namespace Foundry.Tests;

/// <summary>
/// A 40 x 3000 PNG - aspect ratio 1:75. Scaled to the full width of an A4 content column it
/// resolves to a height many times the page, which is what made QuestPDF throw
/// "conflicting size constraints" and lose the entire spec PDF.
/// </summary>
internal static class TallPngFixture
{
    public static byte[] Bytes => Convert.FromBase64String(B64);

    private const string B64 =
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAu4CAAAAAC29KUIAAAFnElEQVR42u3aQwIABgLAwNS2bds2t7Zt27bdrW3btm3btnnvKQ/IvGPAGkxiWI" +
        "lRJMaWmEhiSokZJGaXmE9iUYmlJVaSWFNiA4nNJbaT2FViH4mDJY6SOFHidInzJC6VuEbiZom7JB6UeELieYnXJN6V+ETia4mfJP6UGEhiSIkR" +
        "JEaXGE9iUolpJGaWmEtiQYn/SSwnsarEOhIbS2wlsaPEHhL7SxwmcazEyRJnSVwocYXE9RK3Sdwr8YjE0xIvSbwp8YHE5xLfSfwq8Y/EoBLDSI" +
        "wsMZbEhBJTSEwvMZvEvBKLSCwlsaLEGhLrS2wmsa3ELhJ7SxwkcaTECRKnSZwrcYnE1RI3Sdwp8YDE4xLPSbwq8Y7ExxJfSfwo8YfEgBJDSAwv" +
        "MZrEuBKTSEwtMZPEnBILSCwusazEKhJrS2wksaXEDhK7S+wncajEMRInSZwpcYHE5RLXSdwqcY/EwxJPSbwo8YbE+xKfSXwr8YvE3xKDSAwtMZ" +
        "LEmBITSEwuMZ3ErBLzSCwssaTEChKrS6wnsanENhI7S+wlcaDEERLHS5wqcY7ExRJXSdwocYfE/RKPSTwr8YrE2xIfSXwp8YPE7xIDSAwuMZzE" +
        "qBLjSEwsMZXEjBJzSMwvsZjEMhIrS6wlsaHEFhLbS+wmsa/EIRJHS/xf4gyJ8yUuk7hW4haJuyUeknhS4gWJ1yXek/hU4huJnyX+khhYYiiJES" +
        "XGkBhfYjKJaSVmkZhbYiGJJSSWl1hNYl2JTSS2lthJYk+JAyQOlzhO4hSJsyUukrhS4gaJ2yXuk3hU4hmJlyXekvhQ4guJ7yV+k+g/9h/7j/3H" +
        "/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+" +
        "w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H" +
        "/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+" +
        "w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H" +
        "/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+" +
        "w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H" +
        "/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+" +
        "w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H" +
        "/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+" +
        "w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H/mP/sf/Yf+w/9h/7j/3H//gXbvR4CDlKh7wAAAAA" +
        "SUVORK5CYII=";
}
