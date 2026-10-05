namespace DropSpace.Infrastructure.Lyrics;

internal static class KugouResponseStatus
{
    // Lyrics replies use status=200 and errcode=200; the catalogue uses status=1/error_code=0.
    public static bool IsSuccess(string field, int code, bool catalog) => field switch
    {
        "status" => code == (catalog ? 1 : 200),
        "errcode" when !catalog => code is 0 or 200,
        _ => code == 0,
    };
}
