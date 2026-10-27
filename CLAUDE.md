# FileSync - Guia de Orquestração e Desenvolvimento

Este repositório contém a solução **FileSync** (.NET 10, C#, WPF, CLI).

## Arquitetura de IA e Orquestração

- **Orquestrador Técnico Principal:** Claude Sonnet 5.5.
  - Responsável por compreender os requisitos do usuário, planejar a arquitetura, coordenar a execução, integrar alterações e validar os resultados finais.
- **Modelo dos Subagentes:** Claude Haiku 5.5 (`claude-haiku-5-5`).
  - Todas as tarefas operacionais delegadas devem utilizar exclusivamente `claude-haiku-5-5`.
  - Não utilizar Sonnet ou Opus para tarefas operacionais delegadas.

## Subagentes Especializados (.claude/agents/)

1. **`filesync-codebase-explorer`**: Inspeção de código, arquitetura, dependências, rotas e mapeamento sem modificação.
2. **`filesync-wpf-ui`**: Desenvolvimento e refatoração de interface WPF/XAML, MVVM, estilos e responsividade.
3. **`filesync-tree-comparison`**: Visualização em árvore, comparação dual, sincronização de rolagem e virtualização.
4. **`filesync-selection-filters`**: Filtros de exibição, busca, seleção em lote e integração com `EffectivePlan()`.
5. **`filesync-profile-manager`**: Gerenciamento e janelas de edição de perfis, persistência e validação.
6. **`filesync-test-engineer`**: Criação e execução de testes automatizados (xUnit), detecção de regressão e validação de build.
7. **`filesync-safety-reviewer`**: Auditoria de segurança, integridade de dados, revisão de exclusões e verificação do Core.

## Regras de Execução e Segurança
- Leitura prévia obrigatória antes de qualquer alteração de código.
- Nenhuma operação destrutiva permitida (`git reset --hard`, `git clean -fd`, etc.).
- Nunca misturar os conceitos: ocultar (UI), desmarcar (plano), remover do plano e excluir fisicamente.
- Execução de testes automatizados é obrigatória após cada ciclo de modificação.
- As regras centrais de integridade e proteção contra exclusão massiva do Core devem ser preservadas.
