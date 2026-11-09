# Processo de release do app Windows

Guia para transformar o código em um MSIX assinado, verificado e com hash, e publicá-lo no GitHub Releases ou na Microsoft Store. O app é offline (AGENTS.md §1): nenhum passo daqui precisa de rede, exceto o timestamp opcional da assinatura.

## 1. Identidade do pacote

- `Identity/@Name` = `com.passwordmanagement.windows` é **final**. O PackageFamilyName e o LocalState (`%LOCALAPPDATA%\Packages\com.passwordmanagement.windows_<hash>`) derivam dele; mudá-lo faz uma instalação nova não achar os cofres da instalação anterior. A primeira release pública é o **último momento** para mudá-lo.
- `Identity/@Publisher` tem de ser **igual, byte a byte**, ao `Subject` do certificado de assinatura. O placeholder `CN=PasswordManager` só serve enquanto o certificado tiver esse Subject; com o certificado real, troque a linha. O `tools/New-Release.ps1` compara os dois e, se divergirem, imprime a linha `Publisher="..."` pronta e para antes de assinar.
- `Identity/@Version` é a versão MSIX de quatro partes; os três primeiros campos são o `<Version>` do `PasswordManager.App.csproj`. As guardas `PackageIdentityTests` mantêm o par.

## 2. Versionamento

1. Suba `<Version>` no `windows/src/PasswordManager.App/PasswordManager.App.csproj` (a versão que as pessoas leem).
2. Ajuste `Identity/@Version` em `windows/src/PasswordManager.App/Package.appxmanifest` para `<Version>.0` (quatro partes).
3. Acrescente a seção `## [<Version>] - AAAA-MM-DD` no topo do `CHANGELOG.md`, abaixo de `## [Não lançado]`, com as notas em pt-BR e o aviso de que não há recuperação de senha sem o Kit de Recuperação.
4. **Nunca reutilize** uma versão já publicada na Store: a loja recusa um pacote cuja versão não seja maior que a última publicada, e o mesmo `.msix` não volta a passar. Suba a versão em vez de reenviar a mesma.

O preflight do `New-Release.ps1` recusa o par de versões divergente e a falta da seção da versão no changelog, antes de qualquer publish.

## 3. Gerar o pacote

A partir de `windows/`:

```
pwsh -NoProfile -File tools/New-Release.ps1 -Clean
pwsh -NoProfile -File tools/New-Release.ps1 -RuntimeIdentifier win-x64
pwsh -NoProfile -File tools/New-Release.ps1 -RuntimeIdentifier win-arm64 -SkipPublish
```

- `-RuntimeIdentifier` aceita `win-x64`, `win-arm64` ou `both` (o padrão).
- `-Clean` é repassado ao `Publish-Release.ps1`, que continua sendo o dono da receita de publish (self-contained, ReadyToRun e um `.msix` por RID em `src/PasswordManager.App/AppPackages/<rid>/**/*_<arch>.msix`, pasta ignorada pelo git). O `New-Release.ps1` chama o `Publish-Release.ps1` como processo filho e depois localiza o pacote pelo glob documentado.
- O driver imprime os tamanhos do publish e do `.msix`, o status da assinatura, o hash e os caminhos dos artefatos.
- `-SkipPublish` assina e gera o hash do pacote que já está em `AppPackages/<rid>/`, sem publicar de novo.

**Ensaio sem assinatura** (não serve para publicar, mas exercita publish, verificação e hash):

```
pwsh -NoProfile -File tools/New-Release.ps1 -RuntimeIdentifier win-x64 -Unsigned
```

Com `-Unsigned`, a assinatura é pulada e `NotSigned` é o status aceito; o hash é gerado do mesmo jeito. Um `.msix` já assinado por um certificado cuja raiz não está confiável devolve `UnknownError` (e não `Valid`/`NotSigned`), então é recusado mesmo com `-Unsigned`: assine-o de novo com o certificado de release ou use um pacote recém-publicado. Sem `-Unsigned` e sem `-CertificatePath`, o script falha antes de publicar e nomeia as duas saídas (`-CertificatePath` com um `.pfx` fora do repositório, ou `-Unsigned`).

**Timestamp** (opcional e com rede): `-TimestampUrl <url do servidor RFC 3161>` acrescenta `/tr <url>` e `/td SHA256` ao `signtool`. Use na release pública se a assinatura precisar continuar verificável depois que o certificado expirar; sem timestamp, a assinatura pode deixar de ser aceita depois do `NotAfter` do certificado.

## 4. Certificado

- O caminho suportado pelo script é um certificado de code signing **OV** (D3), exportado como `.pfx` **com a chave privada** e mantido **fora do repositório** (numa pasta do usuário ou numa mídia removível). O script recusa, por caminho completo, um `.pfx` que esteja dentro da raiz do repositório.
- A senha do `.pfx` vem de `PM_CERT_PASSWORD`. Prefira a variável de ambiente e nunca passe a senha na linha de comando nem grave um literal no script, no repositório ou num arquivo de configuração versionado. Na sessão do PowerShell:

  ```
  $env:PM_CERT_PASSWORD = Read-Host -MaskInput
  pwsh -NoProfile -File tools/New-Release.ps1 -CertificatePath <caminho do .pfx fora do repositório>
  ```

  `-CertificatePassword` existe como alternativa, mas a variável de ambiente é o caminho recomendado. O valor nunca é impresso: o log mostra `***` no lugar da senha.
- Antes de assinar, o script carrega o `.pfx` e exige: extensão de uso de chave de code signing (EKU `1.3.6.1.5.5.7.3.3`), `NotAfter` no futuro, chave privada e `Subject` igual ao `Publisher` do manifesto. Qualquer falha para o processo antes do `signtool`.
- **Azure Trusted Signing** é o caminho alternativo manual: o `New-Release.ps1` assina com `signtool` e um `.pfx`, então uma assinatura por Trusted Signing é feita pela ferramenta/integração da Microsoft, fora do script. Depois de assinar, rode `pwsh -NoProfile -File tools/New-Release.ps1 -SkipPublish -Unsigned` para verificar e gerar o `.sha256` (a verificação aceita `Valid`, mesmo com `-Unsigned`), ou gere o hash com `Get-FileHash` como na seção 5 e confira a assinatura com `Get-AuthenticodeSignature`.
- Nem o certificado nem a senha entram no repositório: `.pfx`, `.p12`, `.cer`, `.snk` e `.key` estão no `.gitignore`, e as guardas `ReleaseSigningTests` reprovam material de chave em `src/` ou `tools/`.

## 5. Hash do pacote

Só depois de `Get-AuthenticodeSignature` devolver `Valid` (ou `NotSigned` sob `-Unsigned`), o script calcula `Get-FileHash -Algorithm SHA256` e grava o arquivo `<msix>.sha256` ao lado do pacote, no formato do `sha256sum`:

```
<hexadecimal> *<nome do arquivo>
```

O `*` indica modo binário no `sha256sum`. O `.sha256` é um artefato de release (fica em `AppPackages/`, fora do git) e acompanha o `.msix` na publicação. Para conferir:

```
Get-FileHash -Algorithm SHA256 .\AppPackages\win-x64\PasswordManager.App_1.0.0.0_Test\PasswordManager.App_1.0.0.0_x64.msix
certutil -hashfile .\AppPackages\win-x64\PasswordManager.App_1.0.0.0_Test\PasswordManager.App_1.0.0.0_x64.msix SHA256
Get-Content .\AppPackages\win-x64\PasswordManager.App_1.0.0.0_Test\PasswordManager.App_1.0.0.0_x64.msix.sha256
```

O hash calculado e o primeiro campo do `.sha256` têm de ser iguais. Use os caminhos que o script imprimiu: o nome do pacote inclui a versão e a arquitetura, e pode mudar entre releases.

## 6. GitHub Releases

1. Confira que a seção `## [<versão>]` do `CHANGELOG.md` está completa e com o aviso de que não há recuperação sem o Kit.
2. Publique o `.msix` de cada RID (`win-x64` e `win-arm64`) e o `.sha256` correspondente como assets da release.
3. Cole as notas da seção da versão do `CHANGELOG.md` como descrição da release; elas são o texto em pt-BR que o usuário lê.
4. Não suba o certificado, a senha, a pasta de publish, o `.appxsym` (recusado) nem nenhum arquivo temporário. O `.msix` assinado e o `.sha256` são os únicos artefatos de release.

## 7. Checklist da Store

- [ ] Versão nova: `<Version>` do csproj, `Identity/@Version` do manifesto e a seção do `CHANGELOG.md` batem, e a versão é maior que a última publicada.
- [ ] `Identity/@Publisher` igual ao DN de publisher do Partner Center, byte a byte.
- [ ] Nenhuma capability de rede no manifesto (`internetClient`, `internetClientServer`, `privateNetworkClientServer`, `enterpriseAuthentication`, `proximity`): só `runFullTrust`.
- [ ] Pacote assinado (`Get-AuthenticodeSignature` = `Valid`) para x64 e ARM64, com o `.sha256` conferido.
- [ ] Pacote de símbolos **não** enviado: ele é recusado em 1.0 (sem telemetria/crash reporting, AGENTS.md §1 e §7; a Store não exige).
- [ ] Texto da listagem e notas em pt-BR vindos do `CHANGELOG.md`, com o aviso de que não há recuperação de senha sem o Kit de Recuperação.
- [ ] Nenhum certificado, senha, cofre real, exportação ou artefato temporário no repositório (`git status` limpo).

## 8. O que nunca entra no repositório

- `.pfx`, `.p12`, `.cer`, `.snk`, `.key`, senha do certificado, cofres reais e exportações.
- `AppPackages/` e os `.sha256`, que são ignorados pelo git e viajam só como assets da release.
- A senha do certificado só existe em `PM_CERT_PASSWORD` durante a sessão; não a escreva em arquivo nenhum.
