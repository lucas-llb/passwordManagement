# Modelo de ameaças (resumo)

## O que protegemos

As entradas do cofre (senhas, usuários, URLs, notas) e a senha mestra.

## Contra quem / o quê

| Ameaça | Proteção |
|---|---|
| Alguém copia o arquivo do cofre (backup, disco roubado, exportação vazada) | Argon2id + XChaCha20-Poly1305; nada legível sem a senha |
| Adulteração do arquivo | Cabeçalho e corpo autenticados (AEAD) |
| Pessoa com acesso ao aparelho desbloqueado | Bloqueio automático por inatividade e ao sair do app; biometria exige `CryptoObject` |
| Captura de tela / gravação / miniatura em "recentes" | `FLAG_SECURE` (Android), `WDA_EXCLUDEFROMCAPTURE` (Windows) |
| Senha copiada esquecida na área de transferência | Limpeza após 30 s; fora do histórico e do clipboard em nuvem |
| Vazamento por rede ou telemetria | Sem permissão/capability de internet; sem dependências de rede |
| Vazamento por logs | Proibido logar segredos; `toString()` das entradas não mostra segredos |

## Fora do alcance

- Malware com privilégios no dispositivo enquanto o cofre está aberto (keylogger, leitura de memória).
- Senha mestra fraca escolhida pelo usuário (mitigado só pelo medidor de força e pelo custo do Argon2id).
- Aparelho com root/jailbreak.
