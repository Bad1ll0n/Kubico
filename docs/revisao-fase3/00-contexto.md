# Kubico — Revisão fase 3: contexto comum

Repositório: C:\Users\Badillon\Projetos\Kubico, branch `mvp-continuacao` (commit 60bb5a1). Lê primeiro o `CLAUDE.md` (regras e decisões) e o README.

## Objetivo do produto (da especificação)
Uma só app para gerir a vida da casa, partilhada por todo o agregado com permissões por membro.
- Para quem: famílias com crianças (núcleo), casais, casas partilhadas, quem cuida de pais idosos ou tem guarda partilhada.
- Concorrência: Cozi, FamilyWall, OurHome, AnyList. Diferenciadores: casa inteligente na mesma app, equipamentos e garantias, funcionalidades para Portugal (IRS, IPO, IUC, feriados), modo tablet de cozinha como painel central, IA em linguagem natural.
- Requisitos transversais "10/10": tempo real e offline-first; avisos inteligentes; modo tablet/kiosk; widgets; onboarding em menos de 2 minutos com modelos; RGPD e 2FA; controlo parental, sem anúncios nem compras para menores; histórico com desfazer; acessibilidade (leitores de ecrã, texto grande, modo simples para avós e crianças); pt-PT e inglês desde o início.
- MVP: agregados e contas, calendário, tarefas e rotinas (pontos, recompensas, mesada), compras, avisos.
- Usado por crianças e com dados sensíveis: a confiança é central.

## Já feito (não repetir)
- Fase 1 (c71d529): dados certos e segurança — 45 itens corrigidos, 2 em parte (lista original perdida).
- Fase 2 (60bb5a1): desempenho.
- P18 (uma só instância da API) é limitação aceite; só comentar se houver novidade.

## Estado conhecido
- Backend: build -warnaserror limpo, 292 testes passam. App: analyze limpo, 1 teste falha (`app/test/fase1_test.dart:210`, T1 — "amanhã" com DateTime.now()+24h falha no dia da mudança de hora, 25/10/2026).
- Aviso EF Core em runtime (Production): consulta com Skip/Take sem OrderBy.
- Stack corre em Docker (`docker compose`, API em http://localhost:5281, Production).

## Regras para os revisores
- Perfil sénior: exigência alta, conclusões concretas com ficheiro:linha, cenário de falha e correção proposta.
- NÃO alterar ficheiros do repositório, não fazer commits, não mudar de branch. Podes correr builds/testes/comandos de leitura.
- Escreve o relatório nesta pasta, no ficheiro indicado, em português de Portugal, ordenado por gravidade (Crítico, Alto, Médio, Baixo), com ids do teu prefixo (ex.: ENG-01). No fim, um "Top 5" do que mais vale a pena fazer.
