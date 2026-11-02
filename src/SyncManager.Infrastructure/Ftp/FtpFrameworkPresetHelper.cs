using SyncManager.Core.Enums;

namespace SyncManager.Infrastructure.Ftp;

public static class FtpFrameworkPresetHelper
{
    public static IReadOnlyList<string> GetPresetExclusionPatterns(WebFrameworkPreset preset)
    {
        return preset switch
        {
            WebFrameworkPreset.WordPress => new[]
            {
                "wp-config.php",
                "wp-content/uploads/",
                "wp-content/cache/",
                "wp-content/upgrade/",
                "wp-content/backup-db/",
                "wp-content/backups/",
                "*.sql",
                "*.sql.gz",
                ".htaccess"
            },
            WebFrameworkPreset.Laravel => new[]
            {
                ".env",
                ".env.*",
                "storage/",
                "vendor/",
                "node_modules/",
                "public/storage",
                "public/hot",
                "*.log"
            },
            WebFrameworkPreset.NodeJs => new[]
            {
                "node_modules/",
                ".env",
                ".env.*",
                ".npm",
                "npm-debug.log*",
                "yarn-debug.log*",
                "yarn-error.log*",
                "pnpm-debug.log*",
                ".next/",
                ".nuxt/",
                "dist/"
            },
            WebFrameworkPreset.DotNet => new[]
            {
                "bin/",
                "obj/",
                "appsettings.Development.json",
                "*.user",
                "*.suo",
                ".vs/",
                "*.log"
            },
            WebFrameworkPreset.AngularReactVue => new[]
            {
                "node_modules/",
                ".env.local",
                ".env.development.local",
                "src/",
                "*.map",
                ".vscode/",
                "npm-debug.log*"
            },
            _ => Array.Empty<string>()
        };
    }

    public static string GetPresetDescription(WebFrameworkPreset preset)
    {
        return preset switch
        {
            WebFrameworkPreset.WordPress => "WordPress (Protege wp-config.php, uploads, cache e bancos SQL)",
            WebFrameworkPreset.Laravel => "Laravel (Protege .env, storage/, vendor/, logs e uploads locais)",
            WebFrameworkPreset.NodeJs => "Node.js (Ignora node_modules/, arquivos .env e logs de pacotes)",
            WebFrameworkPreset.DotNet => ".NET (Ignora bin/, obj/, configurações de desenvolvimento e .vs)",
            WebFrameworkPreset.AngularReactVue => "Frontend SPA (Ignora node_modules/, código-fonte src/ e source-maps)",
            _ => "Personalizado / Sem predefinição"
        };
    }

    public static IReadOnlyList<string> LoadFtpIgnorePatterns(string localRootDirectory)
    {
        var ftpIgnorePath = Path.Combine(localRootDirectory, ".ftpignore");
        if (!File.Exists(ftpIgnorePath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var lines = File.ReadAllLines(ftpIgnorePath);
            var patterns = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }
                patterns.Add(trimmed);
            }

            return patterns;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static List<string> ResolveAllExclusions(
        IReadOnlyList<string> taskExclusions,
        WebFrameworkPreset preset,
        string localRootDirectory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Exclusões nativas da tarefa
        foreach (var rule in taskExclusions)
        {
            if (!string.IsNullOrWhiteSpace(rule))
            {
                result.Add(rule.Trim());
            }
        }

        // 2. Exclusões do preset do framework
        var presetRules = GetPresetExclusionPatterns(preset);
        foreach (var rule in presetRules)
        {
            result.Add(rule);
        }

        // 3. Exclusões do arquivo .ftpignore
        if (Directory.Exists(localRootDirectory))
        {
            var ftpIgnoreRules = LoadFtpIgnorePatterns(localRootDirectory);
            foreach (var rule in ftpIgnoreRules)
            {
                result.Add(rule);
            }
        }

        // 4. Sempre ignorar arquivos de controle de versão por segurança básica
        result.Add(".git/");
        result.Add(".svn/");
        result.Add(".DS_Store");
        result.Add("Thumbs.db");

        return result.ToList();
    }
}
