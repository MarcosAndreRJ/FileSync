using SyncManager.Core.Enums;

namespace SyncManager.Wpf.ViewModels;

/// <summary>Métodos e estratégias de exclusão oferecidos na tela de comparação e no editor de perfil (uma só lista).</summary>
public static class MethodCatalog
{
    public static readonly IReadOnlyList<MethodOption> Methods = new[]
    {
        new MethodOption(SyncMethod.MirrorSourceToTarget, Presentation.MethodName(SyncMethod.MirrorSourceToTarget),
            "O Target fica igual ao Source. Arquivos extras no Target são removidos (para a pasta de segurança, por padrão)."),
        new MethodOption(SyncMethod.ReplicationAugment, Presentation.MethodName(SyncMethod.ReplicationAugment),
            "Copia para o Target o que só existe no Source. Nunca atualiza nem exclui."),
        new MethodOption(SyncMethod.ReplicationRefresh, Presentation.MethodName(SyncMethod.ReplicationRefresh),
            "Atualiza no Target os arquivos que existem nos dois lados quando o Source é mais novo. Não copia novos, não exclui."),
        new MethodOption(SyncMethod.ReplicationUpdate, Presentation.MethodName(SyncMethod.ReplicationUpdate),
            "Copia arquivos novos e atualiza os modificados (Source mais novo/diferente). Nunca exclui nem sobrescreve Target mais novo."),
        new MethodOption(SyncMethod.Synchronization, Presentation.MethodName(SyncMethod.Synchronization),
            "Duas mãos: o que mudou em cada lado vai para o outro, guiado pelo histórico da última sincronização (não pela data). " +
            "Se os dois lados mudaram o mesmo arquivo, vira CONFLITO para você decidir. Exclusões são propagadas (para a pasta de segurança). " +
            "Salve como perfil para ter histórico.")
    };

    public static readonly IReadOnlyList<DeleteOption> DeleteOptions = new[]
    {
        new DeleteOption(DeleteStrategy.SafetyFolder, "Mover para pasta de segurança (recomendado)"),
        new DeleteOption(DeleteStrategy.RecycleBin, "Mover para a Lixeira (somente disco local)"),
        new DeleteOption(DeleteStrategy.Permanent, "Excluir permanentemente")
    };
}
