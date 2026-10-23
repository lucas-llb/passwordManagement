# passwordManagement

Gerenciador de senhas offline, simples e leve, com apps nativos para **Windows** (.NET 10 + WinUI 3) e **Android** (Kotlin + Jetpack Compose). Cada perfil tem sua própria senha mestra; nada sai do dispositivo, a não ser por exportação feita pelo usuário.

Detalhes do projeto, regras de segurança e convenções: [AGENTS.md](AGENTS.md).

## Pré-requisitos

- **Windows:** .NET SDK 10 (fixado em `windows/global.json`).
- **Android:** JDK 17 ou mais novo e Android SDK com a plataforma 37 (o Android Studio instala os dois). O Gradle vem pelo wrapper.

## Comandos

```bash
# Windows
dotnet build windows/PasswordManager.slnx
dotnet test --solution windows/PasswordManager.slnx
dotnet run --project windows/src/PasswordManager.App

# Android (dentro de android/)
./gradlew :core:testDebugUnitTest :app:testDebugUnitTest
./gradlew lint
./gradlew :app:assembleDebug
```
