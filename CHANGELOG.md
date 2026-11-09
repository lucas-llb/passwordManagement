# Registro de alterações

Todas as mudanças notáveis deste projeto ficam neste arquivo, em pt-BR, no formato do [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/). As versões seguem o versionamento semântico (`MAJOR.MINOR.PATCH`); o MSIX usa a forma de quatro partes `MAJOR.MINOR.PATCH.0`.

O `tools/New-Release.ps1` exige uma seção `## [<versão>]` neste arquivo cujo título bata com o `<Version>` do `PasswordManager.App.csproj`. A seção da versão é a origem das notas publicadas no GitHub Releases e na Store.

## [Não lançado]

Nada por enquanto.

## [1.0.0] - 2026-11-09

### Aviso importante

**Não há recuperação de senha.** O app não guarda a senha mestra, não tem pergunta secreta e não envia nada para lugar nenhum: quem esquecer a senha mestra e não tiver anotado o **Kit de Recuperação** perde o cofre para sempre. O kit é mostrado uma única vez na criação do perfil (e pode ser trocado nas configurações); anote-o e guarde-o fora do computador.

### Adicionado

- Gerenciador de senhas offline com múltiplos perfis no mesmo dispositivo: cada perfil tem o próprio cofre `.pmvault` e a própria senha mestra.
- Cofre no formato v1: Argon2id (64 MiB, 3 iterações, 1 thread) para a senha mestra e XChaCha20-Poly1305 para a Vault Key e o corpo, com gravação atômica e cópia `.bak`.
- Kit de Recuperação mostrado uma vez na criação do perfil e renovável nas configurações; é o único caminho de volta se a senha mestra for esquecida.
- Entradas com título, usuário, senha, URL, notas, pasta, favorito e datas de criação/edição.
- Busca ao vivo e filtros "Só favoritas" e por pasta.
- Gerador de senhas embutido (8 a 128 caracteres, padrão 20) com maiúsculas, números e símbolos, usando o gerador criptográfico do sistema.
- Cópia para a área de transferência que se apaga em 30 s, fica fora do histórico e da nuvem e também é limpa ao bloquear.
- Bloqueio automático por inatividade (padrão 5 min), ao bloquear a sessão do Windows e, se ligado, ao minimizar.
- Configurações em uma tela: tempo de inatividade, bloqueio ao minimizar, troca da senha mestra, novo Kit de Recuperação e atalhos para exportar e importar.
- Exportação `.pmvault` com senha própria e exportação CSV (`name,url,username,password,notes,folder`), com aviso de arquivo legível; importação de `.pmvault` e de CSV do Bitwarden, do KeePassXC e do KeePass 1.x, com prévia e tratamento de duplicados.
- Interface pt-BR com tema claro/escuro do sistema, contraste AA, navegação por teclado e nomes para leitores de tela.
- Janela fora das capturas de tela enquanto o cofre está aberto.
- MSIX empacotado (`com.passwordmanagement.windows`), com a capability `runFullTrust` e **sem** `internetClient` nem outra capability de rede.
- Ferramentas de release: `tools/Publish-Release.ps1` (publish self-contained com ReadyToRun e um `.msix` por RID), `tools/Measure-Release.ps1` (tamanho e inicialização) e `tools/New-Release.ps1` (preflight, assinatura, verificação e hash SHA-256).

### Segurança

- Nenhum código de rede, telemetria ou crash reporting: o app é offline de verdade.
- Chaves e senha mestra só existem em claro na memória, são zeradas ao bloquear e nunca vão para log.
- A assinatura do MSIX usa um certificado mantido **fora** do repositório e nunca é commitada; o pacote é verificado com `Get-AuthenticodeSignature` antes de o hash ser gerado.
- O pacote de símbolos é recusado: sem crash reporting remoto, a Store não exige `.appxsym`.

### Notas de lançamento

- Windows 10 2004 (build 19041) ou mais recente e Windows 11, em x64 e ARM64.
- O `Identity/@Publisher` do manifesto é o placeholder `CN=PasswordManager` até o certificado de release existir; o processo de release (`docs/release-process.md`) troca essa linha pelo Subject do certificado antes da assinatura.
- O app não recupera senha sem o Kit de Recuperação; não há suporte a Windows Hello nem autofill nesta versão.
