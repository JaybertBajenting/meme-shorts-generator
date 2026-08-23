using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>Produces the white rage-comic panel PNG for one beat.</summary>
public interface IPanelRenderer
{
    Task GenerateAsync(Beat beat, int width, int height, string outputPngPath, CancellationToken ct = default);
}
