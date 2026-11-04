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
| Arquivo de importação hostil (um `.pmvault` ou CSV malformado, gigante ou adulterado que o usuário escolhe) | Limites antes de alocar (32 MiB, 20.000 itens, 65.536 caracteres por campo, 64 colunas); CSV em UTF-8 estrito e recusado inteiro quando a estrutura quebra; um `.pmvault` só chega à prévia depois do Argon2id e da tag AEAD válida no slot e no corpo, e a senha errada e o arquivo adulterado têm uma só mensagem; nada é gravado antes de "Confirmar importação" (uma gravação atômica, com `.bak`), a prévia nunca mostra senha e o arquivo de origem nunca é alterado; o bloqueio não espera pelo custo de Argon2id que o arquivo escolheu. Sobra: um arquivo de custo máximo mantém a página ocupada por dezenas de segundos |
| Vazamento por rede ou telemetria | Sem permissão/capability de internet; sem dependências de rede |
| Vazamento por logs | Proibido logar segredos; `toString()` das entradas não mostra segredos |

## Fora do alcance

- Malware com privilégios no dispositivo enquanto o cofre está aberto (keylogger, leitura de memória).
- Senha mestra fraca escolhida pelo usuário (mitigado só pelo medidor de força e pelo custo do Argon2id).
- Aparelho com root/jailbreak.
