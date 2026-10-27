using SyncManager.Core.Abstractions;
using SyncManager.Core.Enums;
using SyncManager.Core.Models;
using SyncManager.Core.Paths;

namespace SyncManager.Application;

public sealed record PreflightResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Valida perfil e raízes antes de qualquer scan. Raiz inacessível NUNCA é tratada como pasta vazia.</summary>
public sealed class PreflightValidator
{
    private readonly IFileSystem _fs;

    public PreflightValidator(IFileSystem fs) => _fs = fs;

    public async Task<PreflightResult> ValidateAsync(SyncProfile p, bool requireWrite, CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(p.SourcePath)) errors.Add("Informe a pasta Source.");
        if (string.IsNullOrWhiteSpace(p.TargetPath)) errors.Add("Informe a pasta Target.");
        if (errors.Count > 0) return new PreflightResult(errors);

        string source, target;
        try
        {
            source = PathUtil.NormalizeRoot(p.SourcePath);
            target = PathUtil.NormalizeRoot(p.TargetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PreflightResult(new[] { "Caminho inválido: " + ex.Message });
        }

        if (PathUtil.IsSameOrInside(source, target) || PathUtil.IsSameOrInside(target, source))
            errors.Add("Source e Target não podem ser a mesma pasta nem uma estar dentro da outra.");

        if (p.DeleteStrategy == DeleteStrategy.RecycleBin && (PathUtil.IsUnc(target) || (p.Method == SyncMethod.Synchronization && PathUtil.IsUnc(source))))
            errors.Add("A Lixeira não funciona em caminhos de rede (os arquivos seriam excluídos permanentemente). Use a pasta de segurança.");

        if (!string.IsNullOrWhiteSpace(p.SafetyFolderPath))
        {
            try
            {
                var safety = PathUtil.NormalizeRoot(p.SafetyFolderPath);
                if (PathUtil.IsSameOrInside(source, safety) || PathUtil.IsSameOrInside(target, safety))
                    errors.Add("A pasta de segurança personalizada não pode ficar dentro do Source nem do Target.");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add("Pasta de segurança inválida: " + ex.Message);
            }
        }


        if (errors.Count > 0) return new PreflightResult(errors);

        var src = await _fs.CheckRootAsync(source, false, ct);
        if (!src.Exists) errors.Add($"Source inacessível: {src.Error} ({source})");
        else if (!src.CanRead) errors.Add($"Sem permissão de leitura no Source: {src.Error}");

        var tgt = await _fs.CheckRootAsync(target, requireWrite, ct);
        if (!tgt.Exists) errors.Add($"Target inacessível: {tgt.Error} ({target}). Nenhuma exclusão será planejada com o Target offline.");
        else if (!tgt.CanRead) errors.Add($"Sem permissão de leitura no Target: {tgt.Error}");
        else if (requireWrite && !tgt.CanWrite) errors.Add($"Sem permissão de escrita no Target: {tgt.Error}");

        return new PreflightResult(errors);
    }
}
