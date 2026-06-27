namespace FactorioLibrary.Objects;

public class FactorioCredentials(string username = "", string token = "")
{
    public string Username { get; set; } = username;
    public string Token { get; set; } = token;
}
