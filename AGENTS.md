# AGENTS.md — passwordManagement

Guia para agentes de IA (e pessoas) que trabalham neste repositório. Leia inteiro antes de mudar código.

## 1. O que é o projeto

Gerenciador de senhas **offline**, **simples** e **leve**, com dois apps nativos:

| Plataforma | Stack |
|---|---|
| Windows 10/11 | C# / .NET 10 + WinUI 3 (Windows App SDK) |
| Android | Kotlin + Jetpack Compose |

Princípios, em ordem de prioridade:

1. **Segurança** — os dados só existem em claro na memória, depois de desbloquear com a senha mestra.
2. **Offline de verdade** — nenhum código de rede, nenhuma telemetria, nenhum sync em nuvem.
3. **Simplicidade** — poucas telas, poucas dependências, interface amigável.
4. **Leveza** — inicialização rápida, pacote pequeno, pouco consumo de memória.

### Funcionalidades (escopo do MVP)

- Múltiplos usuários (perfis) no mesmo dispositivo; **cada perfil tem seu próprio cofre e sua própria senha mestra**.
- A senha mestra deriva a chave que descriptografa o cofre. Sem ela, nada pode ser lido.
- CRUD de entradas: título, usuário, senha, URL, notas, pasta/tag, favorito, datas de criação/edição.
- Busca local, gerador de senhas, copiar para a área de transferência com limpeza automática.
- **Android:** desbloqueio por biometria (opcional, ativado pelo usuário).
- **Exportar** (e importar) senhas — ver seção 5.

### Fora de escopo (não implementar sem pedido explícito)

Sync/nuvem, conta online, extensão de navegador, compartilhamento entre usuários, analytics, crash reporting remoto, anúncios. Autofill do Android e Windows Hello são candidatos para uma fase futura.

## 2. Estrutura do repositório

```
/AGENTS.md                       # este guia (CLAUDE.md só importa este arquivo)
/docs/
  vault-format.md                # especificação do formato do cofre (fonte da verdade)
  threat-model.md                # o que protegemos e de quem
/spec/test-vectors/              # vetores de teste usados pelas DUAS plataformas (só dados fictícios)
/windows/
  PasswordManager.slnx
  global.json                    # fixa o SDK .NET 10 e o runner Microsoft.Testing.Platform
  Directory.Build.props          # Nullable, warnings como erro, analisadores, lockfiles
  Directory.Packages.props       # versões NuGet centralizadas (CPM)
  src/PasswordManager.Core/      # cripto, cofre, modelos — sem dependência de UI
  src/PasswordManager.App/       # WinUI 3 (MVVM com CommunityToolkit.Mvvm), textos em Strings/pt-BR/Resources.resw
  tests/PasswordManager.Core.Tests/   # xUnit v3
/android/
  settings.gradle.kts
  gradle/libs.versions.toml      # version catalog (todas as versões ficam aqui)
  core/                          # biblioteca Android: cripto, cofre, modelos — sem UI
  app/                           # Compose UI, ViewModels, biometria
```

- Pacotes: Windows `PasswordManager.*`; Android `com.passwordmanagement.core` e `com.passwordmanagement.android` (também o `applicationId`).
- O AGP 9 tem Kotlin embutido: **não** aplicar o plugin `org.jetbrains.kotlin.android` nos módulos. Ele só fica declarado no `build.gradle.kts` raiz (`apply false`) para fixar a versão do Kotlin.
- `lazysodium-android` e `jna` entram como `aar`. A versão do `jna` acompanha a usada pelo lazysodium.

Regra: **toda a lógica de cripto e de formato do cofre fica no módulo `Core`/`core`**, isolada e testável sem UI. A UI só chama `unlock`, `lock`, `save`, `export` etc.

## 3. Arquitetura de segurança (obrigatória)

### 3.1 Hierarquia de chaves

```
senha mestra ──Argon2id(salt)──► KEK (256 bits)
                                   │ cifra/decifra
                                   ▼
                        Vault Key (256 bits, aleatória) ──► cifra o conteúdo do cofre
                                   ▲
              (Android) chave do Keystore protegida por biometria também envolve a Vault Key
```

- A **Vault Key** é gerada uma vez com CSPRNG e guardada apenas *envolvida* (wrapped) pela KEK.
- Trocar a senha mestra = re-envolver a Vault Key; o conteúdo não precisa ser re-cifrado.
- A biometria é só **outro jeito de destravar a mesma Vault Key**, nunca substitui a criptografia.

### 3.2 Primitivas (não inventar cripto)

| Uso | Algoritmo | Biblioteca |
|---|---|---|
| Derivação da senha mestra | **Argon2id** | libsodium (`crypto_pwhash`) |
| Cifra autenticada | **XChaCha20-Poly1305** (nonce 192 bits aleatório) | libsodium (`crypto_aead_xchacha20poly1305_ietf`) |
| Aleatoriedade | CSPRNG do sistema | `RandomNumberGenerator` (.NET) / `SecureRandom` (Kotlin) ou `randombytes_buf` |

- Windows: `NSec.Cryptography` (wrapper de libsodium) ou binding equivalente mantido.
- Android: `lazysodium-android`.
- Usar a **mesma** biblioteca base nas duas plataformas garante que o arquivo de uma abre na outra.
- Parâmetros iniciais do Argon2id: **memória 64 MiB, iterações 3, paralelismo 1**, salt de 16 bytes. Os parâmetros ficam gravados no cabeçalho do cofre para poderem ser aumentados no futuro sem quebrar cofres antigos. Medir em um Android de entrada: o desbloqueio deve ficar entre ~0,5 e 1,5 s.
- **Nunca** usar: AES-ECB, SHA-x puro ou PBKDF2 com poucas iterações para senha, `Random`/`kotlin.random` para segredos, `SecureString`, cripto escrita à mão.

### 3.3 Formato do cofre

- **Um arquivo por perfil** (ex.: `<perfilId>.pmvault`), mesmo formato binário nas duas plataformas.
- Cabeçalho em claro: magic, versão do formato, parâmetros do KDF, salt, Vault Key envolvida (+ nonce).
- Corpo: JSON das entradas, cifrado com a Vault Key. O **cabeçalho entra como AAD**, então qualquer alteração nele invalida o arquivo.
- Escrita **atômica**: grava em arquivo temporário → flush/fsync → renomeia sobre o original. Manter uma cópia `.bak` da versão anterior.
- O nome do perfil pode ficar em claro (para a tela de seleção); **todo o resto** fica cifrado, inclusive títulos e URLs.
- Qualquer mudança de formato: subir a versão, atualizar `docs/vault-format.md`, adicionar vetores em `spec/test-vectors/` e fazer as **duas** plataformas passarem neles.

### 3.4 Senha mestra

- Mínimo de 12 caracteres, com medidor de força offline (ex.: zxcvbn port). Sugerir frase-senha.
- **Não existe recuperação** pelo app: avisar isso de forma clara na criação do perfil.
- Recomendado: gerar um **Kit de Recuperação** (chave aleatória de 128+ bits, exibida uma vez para imprimir/anotar) que também envolve a Vault Key.
- Após tentativas erradas, atraso progressivo na UI (ex.: 1 s, 2 s, 4 s… até 30 s). A proteção real contra força bruta é o Argon2id.
- Nunca guardar a senha mestra nem hash dela para "verificar"; a verificação é o próprio sucesso da decifragem (tag AEAD válida).

### 3.5 Ciclo de vida em memória

- Bloquear automaticamente: por inatividade (padrão 5 min, configurável), ao ir para segundo plano no Android, ao bloquear a sessão do Windows (`SystemEvents.SessionSwitch`) e opcionalmente ao minimizar.
- Ao bloquear: zerar arrays de chave (`CryptographicOperations.ZeroMemory` / `fill(0)`), descartar o modelo decifrado e limpar a UI.
- Preferir `byte[]`/`CharArray` a `string`/`String` para senha mestra e chaves; zerar após o uso. (Em linguagens com GC isso reduz, mas não elimina, cópias em memória — é esperado.)
- **Nunca** logar segredos, nem em debug. Nada de `ToString()` que imprima senha.

### 3.6 Área de transferência

- Limpar 30 s depois de copiar (só se o conteúdo ainda for o que o app copiou).
- Windows: marcar o conteúdo para não entrar no histórico nem no clipboard em nuvem (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0`).
- Android 13+: `ClipDescription.EXTRA_IS_SENSITIVE = true`.

## 4. Configurações recomendadas por plataforma

### 4.1 Android (Kotlin)

- `minSdk 26`, `targetSdk`/`compileSdk` = API estável mais recente. Kotlin 2.x, Compose BOM, Material 3, version catalog (`libs.versions.toml`).
- **Sem permissão `INTERNET`.** Bibliotecas podem injetá-la via manifest merge; bloquear explicitamente:
  ```xml
  <uses-permission android:name="android.permission.INTERNET" tools:node="remove" />
  ```
  e conferir o manifest final (`app/build/intermediates/merged_manifests`) a cada dependência nova.
- `android:allowBackup="false"` e `dataExtractionRules` excluindo os arquivos do cofre (sem backup em nuvem; o usuário usa a exportação).
- `FLAG_SECURE` na Activity: bloqueia screenshot, gravação de tela e miniatura em "recentes".
- Cofre em armazenamento interno do app (`filesDir`). Exportar/importar via Storage Access Framework (`ACTION_CREATE_DOCUMENT` / `ACTION_OPEN_DOCUMENT`) — sem permissão de armazenamento.
- Release: R8/minify ligado, `debuggable false`, assinatura com chave fora do repositório.
- Campos de senha: `KeyboardType.Password`, sem sugestões/aprendizado do teclado (`autoCorrect = false`, `IME_FLAG_NO_PERSONALIZED_LEARNING`).

**Biometria**

- `androidx.biometric` com `BiometricPrompt` + **`CryptoObject`** (não usar só o callback de sucesso — isso é burlável).
- Chave AES-256-GCM no Android Keystore com:
  - `setUserAuthenticationRequired(true)` e `setUserAuthenticationParameters(0, AUTH_BIOMETRIC_STRONG)` (exige biometria a cada uso);
  - `setInvalidatedByBiometricEnrollment(true)` (cadastrar nova digital invalida a chave);
  - `setIsStrongBoxBacked(true)` quando disponível, com fallback.
- Essa chave envolve a Vault Key; o resultado fica num arquivo separado do perfil.
- Biometria só pode ser ativada depois de desbloquear com a senha mestra.
- Exigir a senha mestra de novo: após reiniciar o aparelho, após X dias (padrão 7) e se a chave for invalidada (`KeyPermanentlyInvalidatedException` → desativar biometria e avisar).

### 4.2 Windows (.NET / WinUI 3)

- .NET 10, WinUI 3 com a versão estável mais recente do Windows App SDK, MVVM com `CommunityToolkit.Mvvm`.
- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, analisadores .NET ligados (`<AnalysisLevel>latest-recommended</AnalysisLevel>`).
- Empacotar como **MSIX** sem a capability `internetClient`; publicar self-contained com trimming quando compatível. Por enquanto o app roda sem pacote (`WindowsPackageType=None`) porque ainda não há ícones; migrar para MSIX antes da primeira release.
- Cofre em `%LOCALAPPDATA%\<App>\vaults\` (ou `ApplicationData.Current.LocalFolder` quando empacotado).
- Nenhum pacote NuGet de rede/telemetria (HttpClient, Application Insights, Sentry etc.).
- Exportar/importar via `FileSavePicker`/`FileOpenPicker`.
- Esconder a janela de capturas quando o cofre estiver aberto: `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`.
- Futuro opcional: Windows Hello (`KeyCredentialManager`) no mesmo modelo da biometria do Android.

## 5. Exportação e importação

1. **Exportação cifrada (padrão e recomendada):** arquivo `.pmvault` com uma **senha de exportação** própria (Argon2id + XChaCha20-Poly1305, mesmo formato do cofre). É o jeito de levar senhas do Windows para o Android e vice-versa, e de fazer backup.
2. **Exportação em texto (CSV):** só depois de pedir a senha mestra de novo e de mostrar um aviso claro de que o arquivo fica legível por qualquer um. Usar colunas compatíveis com Bitwarden/KeePass (`name,url,username,password,notes,folder`) para facilitar migração.
3. **Importação:** `.pmvault` próprio e CSV nos formatos acima. Validar tudo e mostrar prévia antes de gravar.

Nunca gravar exportações em pasta temporária ou cache; sempre no local escolhido pelo usuário.

## 6. Interface

- Fluxo: **Selecionar perfil → Desbloquear → Lista (com busca) → Detalhe/Editar**. Configurações em uma tela só.
- Senhas ocultas por padrão, com botão de mostrar e de copiar.
- Gerador de senha embutido no formulário (tamanho, maiúsculas, números, símbolos; padrão 20 caracteres) usando CSPRNG.
- Tema claro/escuro seguindo o sistema. Acessível: rótulos para leitores de tela, alvos de toque ≥ 48 dp, contraste AA.
- Textos da UI em **pt-BR**, em arquivos de recurso (`strings.xml` / `.resw`), prontos para tradução. Código, nomes e commits em inglês.

## 7. Dependências

- Cada dependência nova precisa de motivo claro. Preferir APIs da plataforma.
- Lista permitida de base: libsodium (NSec / lazysodium), CommunityToolkit.Mvvm, AndroidX (Compose, Lifecycle, Biometric), kotlinx.serialization, System.Text.Json.
- Proibido: SDKs de analytics, ads, crash reporting remoto, qualquer coisa que abra conexão de rede.
- Fixar versões (version catalog / Central Package Management) e manter lockfiles.

## 8. Testes

- `Core` das duas plataformas com testes unitários: derivação de chave, wrap/unwrap, cifrar/decifrar, senha errada falha, arquivo adulterado falha (cabeçalho e corpo), troca de senha mestra, export/import.
- **Vetores compartilhados** em `spec/test-vectors/`: um cofre gerado no Windows tem que abrir no Android e vice-versa.
- Comandos:
  - Windows: `dotnet test --solution windows/PasswordManager.slnx`
  - Android: `./gradlew :core:testDebugUnitTest :app:testDebugUnitTest` (em `android/`), e `./gradlew lint`.
- Mudança em cripto ou formato **não** é considerada pronta sem esses testes passando nas duas plataformas.

## 9. Regras para agentes

- Não adicionar código de rede, telemetria ou dependência que exija internet.
- Não alterar o formato do cofre sem seguir a seção 3.3.
- Não implementar cripto própria; usar só as primitivas da seção 3.2.
- Não logar, imprimir, serializar em claro ou commitar segredos (inclusive em testes — usar dados fictícios).
- Não commitar keystores, `.jks`, `.pfx`, `local.properties`, cofres reais ou exportações.
- Manter a solução simples: antes de adicionar tela, opção ou camada, verificar se ela é necessária para o escopo da seção 1.
- Ao terminar uma tarefa, rodar os testes da plataforma afetada e relatar o resultado.
