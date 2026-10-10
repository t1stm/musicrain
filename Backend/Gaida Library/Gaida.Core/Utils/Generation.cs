namespace Gaida.Core.Utils;

public static class Generation
{
    private const string Alphanumerics = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string RandomString(int length)
    {
        return new string(Random.Shared.GetItems<char>(Alphanumerics, length));
    }
}