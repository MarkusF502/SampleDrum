namespace SampleDrum.Core.Simulation;

/// <summary>
/// pt-BR messages for file errors shown to the user by the console tools and the simulation host
/// (.NET's own messages, e.g. "Could not find file ...", are English and depend on the OS language).
/// Messages already produced by SampleDrum (WAV format errors etc.) are kept as they are.
/// </summary>
public static class UserMessages
{
    public static string FileError(Exception ex, string? path = null) => ex switch
    {
        FileNotFoundException f => $"arquivo não encontrado: {f.FileName ?? path ?? "(sem nome)"}",
        DirectoryNotFoundException => path is null
            ? "pasta não encontrada (confira o caminho)"
            : $"pasta não encontrada para o arquivo: {path}",
        UnauthorizedAccessException => path is null
            ? "sem permissão para acessar o arquivo ou a pasta"
            : $"sem permissão para acessar: {path}",
        PathTooLongException => "caminho longo demais",
        IOException when path is not null => $"não foi possível acessar {path} (o arquivo está aberto em outro programa?)",
        IOException => "não foi possível acessar o arquivo (está aberto em outro programa?)",
        _ => ex.Message,
    };
}
