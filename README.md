<div align="center">

<a href="#readme">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/assets/logo-dark.svg">
    <img src=".github/assets/logo-light.svg" alt="Base investimentos" width="449">
  </picture>
</a>

<p>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white">
  <img alt="React 19" src="https://img.shields.io/badge/React-19-61DAFB?style=for-the-badge&logo=react&logoColor=black">
  <img alt="PostgreSQL 17" src="https://img.shields.io/badge/PostgreSQL-17-4169E1?style=for-the-badge&logo=postgresql&logoColor=white">
  <img alt="FIX 4.4" src="https://img.shields.io/badge/FIX-4.4-0F172A?style=for-the-badge">
</p>

<p>
  <a href="#como-rodar"><b>Como rodar</b></a> ·
  <a href="#como-testar"><b>Como testar</b></a> ·
  <a href="#decisões"><b>Decisões</b></a> ·
  <a href="#na-nuvem-aws"><b>Na nuvem</b></a> ·
  <a href="#observabilidade"><b>Observabilidade</b></a>
</p>

</div>

# Base investimentos

Envio de ordens com FIX 4.4. Três aplicações em C#: o OrderGenerator mostra a tela e manda por FIX as ordens de
compra e venda, o OrderAccumulator aceita ou rejeita cada uma conforme o limite de exposição por ativo, e o worker
de métricas manda ao Datadog, a cada 5 minutos, a exposição e a contagem de ordens que lê no banco.

Está no ar em https://h2asgc2sce.execute-api.us-east-1.amazonaws.com (veja [Na nuvem](#na-nuvem-aws)).

## Como rodar

### Com Docker

Precisa do Git e do Docker com o Compose (no Windows e no Mac, o Docker Desktop aberto).

**1. Baixe o projeto:**

```bash
git clone https://github.com/escolaparaprogramadores/flowa_challange.git
cd flowa_challange
```

> [!IMPORTANT]
> Baixe com `git clone`, não pelo botão "Download ZIP". O build da imagem lê o commit da pasta `.git`, que
> o ZIP não traz, e para com o erro `No commit`.

**2. Suba tudo:**

```bash
docker compose up
```

Isso constrói as imagens e sobe o PostgreSQL, o OrderAccumulator, o OrderGenerator e o worker de métricas do Datadog. A primeira vez
demora alguns minutos.

**3. Abra a página:** http://localhost:8080 (só nesta máquina).

`Ctrl+C` para; `docker compose down -v` apaga também o banco.

Se a porta 8080 estiver ocupada: `FLOWA_HTTP_PORT=9080 docker compose up` e abra http://localhost:9080.

### O que precisa estar instalado

Para rodar com Docker não precisa de .NET nem de Node na máquina: o build acontece dentro das imagens.

| Programa | Para quê | Versão usada para testar este README | Como conferir |
|---|---|---|---|
| Git | baixar o projeto | 2.54 | `git --version` |
| Docker Desktop (Windows e Mac) ou Docker Engine (Linux) | construir e rodar as imagens | 29.5.3 | `docker version` |
| Docker Compose v2 ou mais novo, que já vem no Docker Desktop | o `docker compose up` | v5.1.4 | `docker compose version` |

O comando é `docker compose`, com espaço: é o Compose v2 em diante. Se algo falhar e a sua versão for bem mais
antiga que as da tabela, atualize o Docker antes de qualquer outra coisa.

### O que sobe

| Serviço | O que faz | Porta |
|---|---|---|
| `ordergenerator` | a página e a API (`/api/orders`, `/api/exposures`); manda cada ordem por FIX ao OrderAccumulator e lê a exposição e a lista direto no banco | 8080, só em `127.0.0.1` |
| `orderaccumulator` | worker sem HTTP: recebe a ordem por FIX, confere os campos e o limite e responde aceita ou rejeitada; cria as tabelas na primeira subida | 9876, só na rede interna |
| `datadogmetrics` | worker sem porta: a cada 5 minutos lê o banco e manda ao Datadog a exposição de cada ativo e a contagem de ordens aceitas e rejeitadas | nenhuma |
| `postgres` | PostgreSQL 17, banco `flowa` | 5432, só na rede interna |

Na sua máquina não há agente do Datadog, e isso não é erro: o `datadogmetrics` continua de pé e, a cada 5
minutos, só escreve no log o que mandaria (`Symbol exposure gauges sent.` e `Answered order counts sent.`). Para
ver: `docker compose logs datadogmetrics`.

A senha do PostgreSQL vem da variável `POSTGRES_PASSWORD`. Sem ela, o compose usa `flowa_dev`, que é um
valor só de desenvolvimento local: o banco não sai da rede interna do compose.

### Enviar uma ordem aceita e uma rejeitada

O limite é de R$ 100.000.000,00 de exposição por ativo. Uma ordem sozinha não chega lá (a quantidade é menor que
100.000 e o preço, menor que R$ 1.000,00), então a rejeitada vem depois de encher o ativo:

| Passo | Na caixa **Nova ordem** | Clique em | O que aparece |
|---|---|---|---|
| 1. Aceita | **Compra**, **VALE3**, quantidade `99999`, preço `999,99` | **Enviar ordem de compra** | selo **Aceita**; em **Exposição por ativo**, VALE3 vai para R$ 99.998.000,01 |
| 2. Rejeitada | **Compra**, **VALE3**, quantidade `10`, preço `500,00` | **Enviar ordem de compra** | selo **Rejeitada** e a mensagem `Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.`; a exposição de VALE3 não muda |

As duas aparecem na lista **Compra/Venda**, com o status de cada uma. Para voltar ao zero, clique em **Deletar tudo**
na lista e confirme, ou rode `docker compose down -v` e `docker compose up` de novo.

### Se algo der errado

| O que acontece | Por quê | O que fazer |
|---|---|---|
| `docker compose up` para logo no começo com erro de conexão com o Docker | o Docker não está rodando | Abra o Docker Desktop e espere ele dizer que está rodando (no Linux, `sudo systemctl start docker`). Depois rode `docker compose up` de novo. |
| Erro dizendo que a porta 8080 já está em uso (`port is already allocated` ou `ports are not available`) | outro programa usa a 8080 | Escolha outra porta com `FLOWA_HTTP_PORT` e abra a página nela. No bash (Mac, Linux, Git Bash): `FLOWA_HTTP_PORT=9080 docker compose up`. No PowerShell: `$env:FLOWA_HTTP_PORT="9080"; docker compose up`. A página fica em http://localhost:9080. |
| A primeira subida demora | o Docker baixa as imagens base e compila os três apps e a tela | Espere. As próximas vezes usam o cache e sobem em segundos. Está pronto quando a página abre. |
| A página abre com exposição zero e lista vazia, logo depois de subir | na primeira subida o OrderAccumulator ainda está criando as tabelas; enquanto isso o OrderGenerator responde exposição zero e lista vazia, sem erro | Espere alguns segundos e recarregue a página. |
| Na primeira subida, o log do `datadogmetrics` mostra um erro `Symbol exposure gauges not sent. Trying again on the next cycle.`, com `42P01: relation "exposures" does not exist` | o worker de métricas leu o banco antes de o OrderAccumulator criar as tabelas | Nada: 5 minutos depois ele lê de novo e escreve `Symbol exposure gauges sent.`. A página não é afetada. |
| A primeira ordem volta com **Erro de comunicação** | a sessão FIX entre o OrderGenerator e o OrderAccumulator ainda está ligando | Espere alguns segundos e envie de novo. |
| O build para com `"/.git": not found` | o projeto foi baixado em ZIP, sem a pasta `.git` | Baixe com `git clone`, como no passo 1. |
| No Windows, o `git clone` termina com `Filename too long` | o Windows limita o caminho de um arquivo a 260 letras, e a pasta onde você clonou já usa boa parte delas | Apague a pasta criada e clone de novo numa pasta de caminho curto, como `C:\dev`, ou deixe o Git usar caminhos longos: `git clone -c core.longpaths=true https://github.com/escolaparaprogramadores/flowa_challange.git`. |

<details>
<summary><b>Sem Docker</b> (clique para abrir)</summary>

### Sem Docker

Precisa do .NET 10 SDK, do Node.js 22 e de um PostgreSQL 17 com um banco `flowa` e um usuário `flowa`.
As tabelas são criadas pelo OrderAccumulator quando ele sobe. Os comandos abaixo são para bash (no
Windows, o Git Bash serve) e usam as portas do contrato: 8080 (a página e a API) e 9876 (o FIX).

Primeiro a tela, que é gravada dentro do OrderGenerator:

```bash
cd frontend
npm ci
npm run build
cd ..
```

Depois o OrderAccumulator, num terminal (troque pela senha que você deu ao usuário `flowa`):

```bash
export POSTGRES_PASSWORD=<senha do usuário flowa>
Fix__AcceptorPort=9876 \
ConnectionStrings__Flowa="Host=localhost;Port=5432;Database=flowa;Username=flowa;Password=$POSTGRES_PASSWORD" \
dotnet run --project src/flowa.orderaccumulator-worker-ecs
```

E o OrderGenerator, em outro terminal. Ele lê a lista e a exposição no mesmo banco, com a mesma senha:

```bash
export POSTGRES_PASSWORD=<senha do usuário flowa>
ASPNETCORE_HTTP_PORTS=8080 Fix__AcceptorHost=localhost Fix__AcceptorPort=9876 \
ConnectionStrings__Flowa="Host=localhost;Port=5432;Database=flowa;Username=flowa;Password=$POSTGRES_PASSWORD" \
dotnet run --project src/flowa.ordergenerator-webapi-ecs
```

A página abre em http://localhost:8080. O OrderAccumulator não tem HTTP: ele só escuta o FIX na 9876.
Os dois apps gravam o commit do build e não sobem sem ele (o OrderGenerator mostra em `/version`), então
precisam ser compilados dentro de um clone do git. O worker de métricas não é preciso para a tela funcionar.

</details>

## Como testar

Para os testes, além do Git e do Docker, precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
e do [Node.js 22](https://nodejs.org/). Rode tudo na raiz do clone, com o Docker aberto.

| Tipo | O que confere | Tecnologia | Onde fica |
|---|---|---|---|
| Unidade e integração do .NET | regra de campo, limite de exposição, concorrência no banco, sessão FIX, rotas, métricas do worker e o teste de camadas de cada app | xUnit, Testcontainers (sobe um PostgreSQL de verdade) | `tests/flowa.ordergenerator-webapi-ecs.Tests/`, `tests/flowa.orderaccumulator-worker-ecs.Tests/`, `tests/flowa.datadog-metrics-worker-ecs.Tests/` |
| Integração com o compose | sobe o compose inteiro em projetos separados (portas 18080 e 18093) e confere a ida e volta FIX entre os containers e a religação depois de recriar o OrderAccumulator | xUnit | `tests/IntegrationTests/` |
| Unidade da tela | validação local, formato dos números, chamadas da API | Vitest | `frontend/src/**/*.test.ts` |
| Ponta a ponta da tela | a página inteira contra a aplicação de pé, com e sem o OrderAccumulator | Playwright | `frontend/e2e/` |
| Carga | ~15 ordens por segundo durante 5 minutos | k6 | `performance test/` ([Teste de carga](#teste-de-carga)) |

**Testes do .NET** (os de integração com o compose clonam o repositório, então também usam o `git`):

```bash
dotnet build Flowa.slnx
dotnet test Flowa.slnx --filter "Category!=Integration"
dotnet test tests/IntegrationTests/IntegrationTests.csproj
```

**Testes da tela.** Os de ponta a ponta precisam da aplicação de pé: deixe o `docker compose up` rodando em outro
terminal. Se você trocou a porta, passe a nova também aqui, com `E2E_BASE_URL=http://localhost:9080`.

```bash
cd frontend
npm ci
npm test
npx playwright install chromium
npm run e2e
```

**Ponta a ponta sem o OrderAccumulator.** Com ele parado, a exposição e a lista continuam aparecendo (vêm do
banco) e só o envio de ordem falha. Ainda dentro de `frontend/`:

```bash
docker compose stop orderaccumulator
npx playwright test -c playwright.without-accumulator.config.ts
docker compose start orderaccumulator
```

O CI (`.github/workflows/0-ci-pull-request.yml`) roda tudo isso em cada PR para `develop` e `main`.

## Documentação de negócio

### Para quem é e o que resolve

Uma boleta de ordens de ações para quem opera: ela monta a ordem de compra ou venda, manda pelo protocolo FIX,
que é o padrão do mercado financeiro, e mostra na hora se foi aceita ou rejeitada. O que o sistema protege é o
risco: nenhuma ordem pode deixar a exposição de um ativo passar de R$ 100 milhões.

### Funcionalidades entregues

| Funcionalidade | O que a pessoa faz | O que o sistema responde |
|---|---|---|
| Enviar ordem | escolhe compra ou venda, o ativo (PETR4, VALE3 ou VIIA4), a quantidade e o preço, e envia | **Aceita** ou **Rejeitada**, com o motivo, na caixa de resposta |
| Ver a exposição | olha o quadro **Exposição por ativo** | a exposição de cada ativo e o quanto falta para o limite |
| Ver as ordens | olha a lista **Compra/Venda** | as ordens enviadas, da mais nova para a mais velha, com status e motivo da rejeição, em páginas |
| Apagar tudo | clica em **Deletar tudo** e confirma | apaga todas as ordens e zera a exposição dos três ativos |
| Modo de teste | dá duplo clique no símbolo | a tela para de conferir os campos e envia como está, para ver o OrderAccumulator rejeitar |
| Métricas no Datadog | nada: acontece sozinho | a cada 5 minutos, a exposição de cada ativo e a contagem de ordens aceitas e rejeitadas |

### Regras de negócio

| Regra | Como funciona |
|---|---|
| Exposição | compra soma preço × quantidade; venda subtrai. Pode ficar negativa. |
| Limite | uma ordem só é aceita se a exposição do ativo depois dela ficar, em valor absoluto, até R$ 100.000.000,00. Chegar exatamente no limite é aceito; um centavo acima é rejeitado. |
| Ordem rejeitada | não muda a exposição. |
| Ordem aceita | entra inteira na exposição, com a quantidade pedida, na hora do aceite. |
| Campos | ativo PETR4, VALE3 ou VIIA4; quantidade inteira de 1 a 99.999; preço maior que zero, menor que R$ 1.000,00 e em centavos. Campo fora disso volta rejeitado, com o motivo. |
| Ordem repetida | o mesmo número de ordem (`ClOrdID`) recebe a mesma resposta da primeira vez, sem contar de novo. Com outros dados, é rejeitado como ordem duplicada. |

O porquê de cada regra está em [Decisões](#decisões).

### Fluxos

1. **Enviar uma ordem.** A tela manda a ordem ao OrderGenerator, que a passa por FIX ao OrderAccumulator. Ele
   confere os campos e o limite, grava no banco e responde aceita ou rejeitada. A tela mostra a resposta e
   atualiza a exposição e a lista.
2. **Consultar.** A exposição e a lista vêm do banco, pelo OrderGenerator. Funcionam mesmo com o OrderAccumulator
   parado; só o envio de ordem falha enquanto ele não volta.
3. **Apagar tudo.** O OrderGenerator zera a exposição e apaga as ordens numa transação só.
4. **Métricas.** A cada 5 minutos o worker de métricas lê o banco e manda ao Datadog a exposição e quantas ordens
   foram aceitas e rejeitadas naquele intervalo.

### Glossário

O texto deste README fala em português; o código usa os nomes em inglês abaixo.

| Termo | O que é | No código |
|---|---|---|
| Ordem | pedido de compra ou venda de um ativo | `Order`, no Domain do OrderAccumulator. A que chega pelo FIX é a `IncomingOrder`; a que o OrderGenerator manda é a `OrderToSend`. |
| Ativo | a ação negociada (PETR4, VALE3, VIIA4) | o `Symbol` da ordem |
| Lado | compra ou venda | `OrderSide` |
| Exposição de um ativo | soma das compras aceitas menos a das vendas aceitas, em reais | `SymbolExposure` |
| Limite de exposição | R$ 100.000.000,00 por ativo | `ExposureLimitPolicy` |
| Regra de campo | o que cada campo da ordem pode ter | `OrderFieldPolicy` |
| Resposta da ordem | aceita (`New`) ou rejeitada (`Rejected`) | a mensagem FIX `ExecutionReport` |
| Número da ordem | identificador único de cada ordem no FIX | o `ClOrdID` do FIX, `ClOrdId` no código |

## Tecnologias

- C# no .NET 10 (ASP.NET Core) para as três aplicações
- QuickFIX/n 1.14.1 (`QuickFIXn.Core` e `QuickFIXn.FIX44`) para o protocolo FIX 4.4
- PostgreSQL 17, acessado com Npgsql e Dapper
- React 19, TypeScript e Vite na tela
- xUnit e Testcontainers nos testes do .NET; Vitest e Playwright nos testes da tela
- Docker Compose para subir tudo junto
- GitHub Actions: build e todos os testes em cada PR, e deploy na AWS quando um merge de código chega em `develop`
- AWS (API Gateway, ECS Fargate, RDS), criada só com Terraform
- Datadog para rastros, métricas das ordens e um painel público, com um agente ao lado de cada serviço na AWS
- k6 para o teste de carga, rodado à mão pelo GitHub Actions

## Onde fica cada coisa

| Pasta | O que tem |
|---|---|
| `src/flowa.ordergenerator-webapi-ecs/` | OrderGenerator: a API, a página e o lado que inicia a sessão FIX |
| `src/flowa.orderaccumulator-worker-ecs/` | OrderAccumulator: o worker FIX, a regra do limite e o esquema do banco |
| `src/flowa.datadog-metrics-worker-ecs/` | o worker de métricas do Datadog |
| `src/flowa.commons/` | o código técnico dos três apps: banco, log, observabilidade e o dicionário FIX |
| `tests/` | um projeto de testes por app e `tests/IntegrationTests/`, que sobe o compose |
| `frontend/` | a tela (React + Vite) e os testes dela, de unidade e de ponta a ponta |
| `infra-aws/` | o Terraform da AWS ([Na nuvem](#na-nuvem-aws)) |
| `observability/datadog/` | o Terraform do painel de ordens e exposição ([Observabilidade](#observabilidade)) |
| `performance test/` | o teste de carga com k6 ([Teste de carga](#teste-de-carga)) |
| `docs/` | o contrato entre as partes, os desenhos da arquitetura e os prints dos painéis |
| `.github/workflows/` | CI, deploy, painel do Datadog e teste de carga |

## Decisões

**Quem inicia a sessão FIX.** O OrderGenerator é o initiator e o OrderAccumulator é o acceptor. Quem
decide fica parado esperando, e quem manda a ordem é quem liga. Se o OrderAccumulator cair, o
OrderGenerator tenta religar a cada 2 segundos e responde erro de comunicação para a tela enquanto
isso, sem travar.

**A regra do limite.** A exposição de um ativo é a soma de preço × quantidade das compras aceitas menos
a das vendas aceitas, então pode ficar negativa. Uma ordem só é aceita se a exposição depois dela
ficar, em valor absoluto, até R$ 100.000.000,00. A borda exata é aceita: o enunciado fala em não passar
do limite, e chegar a 100 milhões não passa. Um centavo acima é rejeitado. As duas bordas têm teste
(`tests/flowa.orderaccumulator-worker-ecs.Tests/IntegrationTests/ExposureRulesTests.cs`).

**Concorrência resolvida no banco.** A exposição só muda num `UPDATE` que testa o limite na própria
cláusula `WHERE`
(`src/flowa.orderaccumulator-worker-ecs/Infrastructure/Exposures/Repositories/ExposureRepository.cs`). Se a ordem não couber,
nenhuma linha muda e ela é rejeitada. Como o PostgreSQL trava a linha durante o `UPDATE`, duas ordens
ao mesmo tempo no mesmo ativo não conseguem passar juntas do limite. Isso não depende de lock em
memória e continuaria valendo com mais de uma instância. Há um teste com 200 ordens simultâneas,
repetido cinco vezes.

**PostgreSQL.** A exposição precisa sobreviver a um reinício do OrderAccumulator, e o banco já resolve
a concorrência do jeito acima. As tabelas são criadas na subida, com `IF NOT EXISTS`.

**Ordem repetida.** O `ClOrdID` de cada ordem é único no banco. Se a mesma `NewOrderSingle` chegar duas
vezes, a segunda não mexe na exposição: o OrderAccumulator devolve o mesmo `ExecutionReport` da
primeira vez, com o mesmo resultado e o mesmo motivo. Se o `ClOrdID` repetido vier com outro ativo,
lado, quantidade ou preço, ela volta rejeitada como ordem duplicada (`OrdRejReason` 6) e a primeira
fica como estava.

**Ordem aceita entra inteira na exposição.** O enunciado fala em somar a "quantidade executada". Aqui
nenhuma ordem é executada: o OrderAccumulator só aceita ou rejeita. A ordem aceita volta com
`ExecType = New` (150=0), `CumQty` 0 e `LeavesQty` igual à quantidade, e mesmo assim a quantidade
inteira entra na exposição no momento do aceite. Se a exposição esperasse uma execução, ela ficaria
sempre em zero e o limite nunca barraria nada.

**A regra de campo mora só no OrderAccumulator.** Símbolo, lado, quantidade e preço são validados pelo
OrderAccumulator, no que chega pelo FIX
(`src/flowa.orderaccumulator-worker-ecs/Domain/Orders/ValueObjects/OrderFieldPolicy.cs`). Campo inválido volta como ordem rejeitada, com o motivo em
português, igual a uma ordem rejeitada pelo limite. O que nem cabe numa ordem FIX (campo faltando, tipo
errado, lado desconhecido) o OrderGenerator responde com erro 400, sem mandar nada. A tela mantém um
aviso local que aparece antes do envio, só para avisar cedo: ele não é a proteção, e o OrderAccumulator
valida mesmo que a tela seja burlada.

## Limitações conhecidas

- Não há login nem autenticação, nem nas rotas HTTP nem na sessão FIX. A sessão é reconhecida só pelos
  nomes `ORDERGENERATOR` e `ORDERACCUMULATOR`.
- Os ativos são fixos (PETR4, VALE3 e VIIA4) e o limite é uma constante no código, não configuração.
- A ordem não é executada: ela só é aceita ou rejeitada. Não existe casamento de ordens nem preenchimento.
- As mensagens FIX ficam em memória e a numeração recomeça a cada logon. Uma mensagem perdida não é
  reenviada: se a sessão cai, o OrderGenerator responde na hora que a ordem pode ter sido aceita; sem
  resposta e sem queda, responde o mesmo depois do prazo (5 segundos por padrão).
- A proteção contra ordem repetida vale para o `ClOrdID` no FIX. Se a tela enviar a mesma ordem de novo,
  ela ganha um `ClOrdID` novo e conta como outra ordem.
- Não há migrações versionadas do banco: o esquema é criado na subida do OrderAccumulator.
- Na AWS cada serviço roda uma cópia só. No deploy a cópia velha para antes de a nova subir, então a
  aplicação fica fora do ar por alguns instantes.

## Como funciona

![Desenho da arquitetura local](docs/architecture/arquitetura-local.png)

A tela é servida pelo próprio OrderGenerator. Quando você envia uma ordem, a tela chama
`POST /api/orders`. O OrderGenerator só confere se o pedido tem o formato de uma ordem e manda uma
`NewOrderSingle` (35=D) por FIX para o OrderAccumulator. O OrderAccumulator confere os campos, aplica a
regra do limite no PostgreSQL e responde com um `ExecutionReport` (35=8): `New` quando aceita, `Rejected` com o motivo
quando não aceita. O OrderGenerator devolve essa resposta para a tela.

O painel de exposição chama `GET /api/exposures` no OrderGenerator, que lê a exposição direto no
PostgreSQL, na tabela que o OrderAccumulator grava. A lista de ordens e o "apagar tudo" seguem o mesmo
caminho. As duas pontas do FIX usam o mesmo dicionário, `src/flowa.commons/Fix/FIX44-flowa.xml`. A fonte do desenho fica em `docs/architecture/arquitetura-local.drawio` e o contrato
entre as partes (rotas, mensagens FIX, portas) em `docs/contracts/contracts.md`.

O worker de métricas não fala com nenhum dos dois apps. A cada 5 minutos ele lê no mesmo banco a exposição de cada
ativo e as ordens que chegaram desde a última leitura, e manda as métricas ao agente do Datadog. Na sua máquina não
há agente, então ele só escreve no log o que mandaria. Os nomes do código ficam no [Glossário](#glossário).

### Como o código é organizado

Cada app é um projeto .NET só (`OrderGenerator.csproj`, `OrderAccumulator.csproj` e `DatadogMetrics.csproj`), e o
código técnico que os três usam fica num quarto projeto, a Commons (`src/flowa.commons/Commons.csproj`). Todos
estão na solução `Flowa.slnx`, junto com os projetos de teste. Dentro de cada app, as camadas são pastas, com o namespace igual à pasta
(`Flowa.OrderAccumulator.Domain`, por exemplo):

- **Entrypoint**: a montagem das dependências e a porta de entrada de cada app. No OrderGenerator, as
  rotas HTTP, o tratamento de erro e o log de cada pedido; no OrderAccumulator, que não tem HTTP, a sessão
  FIX; no worker de métricas, o laço de 5 minutos. Recebe o pedido, chama o caso de uso e responde.
- **Application**: os casos de uso. Cada um só organiza o passo a passo, sem regra de negócio.
- **Domain**: as regras do negócio: a ordem, a regra de campo, o limite de exposição. Não usa nenhuma
  biblioteca de fora.
- **Infrastructure**: o SQL do PostgreSQL, o cliente FIX e, no worker datadog-metrics, as métricas do Datadog. Banco e Datadog
  passam pela Commons; fora daqui, só o Entrypoint usa a QuickFIX/n, na sessão FIX do OrderAccumulator.

A **Commons** é a parte técnica dividida pelos três apps: banco (Dapper e Npgsql atrás de `IDatabase`), log,
observabilidade, o envelope `DataMessage`, a base das entidades e o dicionário FIX (`Fix/FIX44-flowa.xml`).

As dependências só apontam para dentro: o Domain não conhece nenhuma outra camada, e só a Commons usa
Dapper, Npgsql e o cliente do Datadog. Um teste de cada app confere isso lendo o código compilado
(`tests/*.Tests/Camadas/LayerDependencyTests.cs`).

Dentro de cada camada há uma pasta por assunto do negócio (`Orders` e `Exposures`) e, dentro dela, uma
pasta por tipo de classe (`UseCases`, `Responses`, `Interfaces`, `Repositories`...).

```
src/flowa.orderaccumulator-worker-ecs/
├─ Entrypoint/
│  ├─ Fix/                  NewOrderSingleConsumer: recebe a ordem FIX e responde o ExecutionReport
│  ├─ BackgroundService/    sessão FIX (acceptor)
│  └─ Observability/
├─ Application/
│  ├─ Orders/       DecideIncomingOrder (UseCases), Responses
│  └─ ErrorHandling/
├─ Domain/
│  ├─ Orders/           Order, regra de campo (OrderFieldPolicy), IOrderRepository
│  ├─ Exposures/        limite de exposição, IExposureRepository
│  └─ DomainServices/   OrderDecisionDomainService
└─ Infrastructure/
   ├─ Orders/, Exposures/    Repositories (SQL); em Orders, também Options
   ├─ Fix/                   rastro na tag 5100 e log da sessão FIX
   ├─ Persistence/           Schema.sql, criado na subida
   └─ DependencyInjection/

src/flowa.ordergenerator-webapi-ecs/
├─ Entrypoint/
│  ├─ Orders/, Exposures/   rotas HTTP e o pedido da ordem
│  └─ DependencyInjection/, ErrorHandling/, Logging/, Observability/
├─ Application/
│  ├─ Orders/       SendOrder, ListOrders e DeleteAllOrders (UseCases), Commands, Responses, Interfaces
│  ├─ Exposures/    GetExposures (UseCases), Responses, Interfaces
│  └─ ErrorHandling/
├─ Domain/
│  ├─ Orders/       OrderToSend, SentOrderResult, formato da ordem
│  └─ Exposures/    SymbolExposure, limite de exposição
└─ Infrastructure/
   ├─ Fix/                   cliente FIX (FixOrderClient), rastro e log da sessão
   ├─ Orders/, Exposures/    Repositories: lista, exposição e "apagar tudo" no PostgreSQL;
   │                         em Orders, também Options (prazo do FIX)
   └─ DependencyInjection/

src/flowa.commons/
├─ Database/, Logging/, Observability/, Responses/, Entities/, DependencyInjection/
└─ Fix/             FIX44-flowa.xml, o dicionário FIX dos dois apps

src/flowa.datadog-metrics-worker-ecs/
├─ Entrypoint/
│  ├─ BackgroundService/    DatadogMetricsBackgroundService: o laço de 5 minutos
│  └─ DependencyInjection/, Observability/
├─ Application/
│  ├─ Exposures/    SendSymbolExposureGauges (UseCases), Interfaces
│  ├─ Orders/       SendAnsweredOrderCounts (UseCases), Commands, Responses, Interfaces
│  └─ ErrorHandling/
├─ Domain/
│  ├─ Exposures/    SymbolExposure
│  └─ Orders/       AnsweredOrderCount, OrderSide, OrderSymbolPolicy
└─ Infrastructure/
   ├─ Exposures/, Orders/    Repositories (leitura no PostgreSQL) e Adapters (métricas do Datadog)
   └─ DependencyInjection/
```

A página não fica no OrderGenerator: o código dela está em `frontend/`, e o build do Vite vai para o
`wwwroot` dele.

No OrderAccumulator, cada agregado tem um repositório: `IOrderRepository` e `IExposureRepository`, com a
interface no Domain e o SQL na Infrastructure. As leituras da tela têm repositório próprio, com a
interface na Application do OrderGenerator: `IStoredOrderRepository` e `ISymbolExposureRepository`.

## Na nuvem (AWS)

A aplicação está publicada em https://h2asgc2sce.execute-api.us-east-1.amazonaws.com. É a mesma tela
da versão local, e as ordens vão para um PostgreSQL de verdade na AWS.

![Desenho da arquitetura na AWS](docs/architecture/arquitetura-aws.png)

O navegador fala só com o API Gateway. Ele passa o pedido por um VPC Link para o OrderGenerator, que
roda no ECS Fargate. O OrderGenerator acha o OrderAccumulator pelo Cloud Map e conversa com ele por FIX,
como na versão local. O OrderAccumulator grava num RDS PostgreSQL que fica numa subnet sem saída para
fora, e o OrderGenerator lê a lista e a exposição nesse mesmo banco. O worker de métricas roda numa terceira
task, sem porta e fora do Cloud Map, porque ninguém o chama: ele lê o mesmo banco a cada 5 minutos e manda as
métricas ao agente do Datadog da própria task. Nenhuma tarefa aceita conexão vinda da internet. Cada serviço roda uma cópia com 0,5 vCPU e
1 GB, que o app divide com o agente do Datadog, o banco é um `db.t3.micro` numa zona só, e o API
Gateway aceita até 20 pedidos por segundo (rajada de 40); acima disso responde 429.

**Como publica.** Os serviços, a rede, o banco, o ECR e os logs são criados pelo Terraform de `infra-aws/`.
A role que a esteira assume e o bucket do state vêm de uma base Terraform separada, fora deste
repositório. Nada é criado pelo console. Quando um PR que muda código é mesclado em `develop`, o
workflow `.github/workflows/2-develop.yml` roda o CI no mesmo commit e, com ele verde, constrói só a
imagem do serviço que mudou, manda para o ECR e roda o `terraform apply`. Merge só de texto (`*.md` e
`docs/`) não publica nada. O GitHub entra na AWS por OIDC, com uma credencial temporária, sem chave
guardada no repositório. A fonte do desenho fica em `docs/architecture/arquitetura-aws.drawio`.

## Observabilidade

São três painéis no Datadog, todos públicos e sem login. Os prints são da noite do teste de carga
(03/10/2026, horário de Brasília).

**[Four Golden Signals](https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-a9e17306767d8f22537a7acb53350942)**:
latência, tráfego, erros e saturação dos dois serviços.

![Painel Four Golden Signals](docs/observability/painel-four-golden-signals.png)

**[Ordens e exposição](https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-cb393cd3b3760676912c981fa71f3372)**:
taxa de aceite, ordens aceitas e rejeitadas e a exposição de cada ativo perto do limite de 100 milhões.

![Painel de ordens e exposição](docs/observability/painel-ordens-e-exposicao.png)

**[Jornada da ordem](https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-b5d1b14c994996116b3fe646ff8d78d5)**:
o rastro de cada ordem, do `POST /api/orders` até o Postgres, passando pelo FIX, com o tempo de cada etapa.

![Painel da jornada da ordem](docs/observability/painel-jornada-da-ordem.png)

Na AWS, cada task roda um agente do Datadog ao lado do app. Os dois apps mandam rastros para o agente
em `localhost:8126`, o worker datadog-metrics manda as métricas em `localhost:8125`, e o agente envia
tudo ao Datadog por HTTPS. Uma ordem
aparece como um rastro só, da tela até o OrderAccumulator: o OrderGenerator põe o contexto do rastro
numa tag FIX própria da `NewOrderSingle`, a 5100 (`TraceParent`), e o OrderAccumulator continua o
mesmo rastro (`Infrastructure/Fix/FixOrderTraceProvider.cs` em cada app). A cada 5 minutos, o worker
datadog-metrics lê o banco, conta `flowa.ordens.aceitas` e `flowa.ordens.rejeitadas` por ativo e lado e
publica `flowa.exposicao` por ativo (`src/flowa.datadog-metrics-worker-ecs/Infrastructure/Orders/Adapters/DatadogOrderMetricsAdapter.cs`
e `src/flowa.datadog-metrics-worker-ecs/Infrastructure/Exposures/Adapters/DatadogExposureMetricsAdapter.cs`).
O OrderAccumulator não manda métrica. O ClOrdID não vira etiqueta, para o número de séries ficar pequeno.

A esteira só põe o agente nas tasks quando o cofre do Datadog no Secrets Manager já tem a chave
(`infra-aws/datadog-agente.tf`, variável `datadog_ligado`). O painel de ordens e exposição, com todos os
gráficos do print, vive no Terraform de `observability/datadog/`, aplicado pelo workflow
`.github/workflows/2-develop-painel-datadog.yml`: ordens e exposição vêm do serviço `datadog-metrics`, e os
gráficos de saúde, dos rastros do OrderAccumulator. Os outros dois painéis foram montados direto no
Datadog e não estão no repositório. As chaves do Datadog ficam no Secrets Manager e nos secrets do GitHub, nunca no
repositório. A conta do Datadog está no período de teste grátis até 15/10/2026; sem
plano contratado, os três painéis param de receber dado novo depois disso.

### Teste de carga

O k6 (`performance test/carga-ordens.js`) manda cerca de 15 ordens por segundo durante 5 minutos, abaixo do limite
de 20 por segundo do API Gateway. Em PETR4, VALE3 e VIIA4 ele provoca três resultados:

- **Aceitas:** pares de compra e venda do mesmo ativo, quantidade e preço, que se compensam.
- **Rejeitadas por campo inválido:** quantidade 100000, que passa pelo FIX e volta rejeitada.
- **Rejeitadas por limite:** ordens grandes enchem a exposição até a seguinte passar de
  R$ 100.000.000,00 e voltar rejeitada. Depois o k6 desfaz o que a API aceitou, olhando a exposição real.

O teste reprova se faltar algum dos três resultados em algum ativo, se a exposição de algum ativo não
voltar ao valor de antes, se o k6 não conseguir manter o ritmo ou se a taxa de erro chegar a 1%.

Medição na máquina, contra o `docker compose` do commit 62979f4 (5 minutos, 4523 ordens):

| Ativo | Aceitas | Rejeitadas por campo | Rejeitadas por limite | Exposição antes e depois |
|---|---|---|---|---|
| PETR4 | 1406 | 101 | 2 | igual |
| VALE3 | 1404 | 101 | 2 | igual |
| VIIA4 | 1404 | 101 | 2 | igual |

| Requisições por minuto | Taxa de erro | P80 | P90 | P95 | P99 |
|---|---|---|---|---|---|
| 904 | 0,00% | 5 ms | 5 ms | 5 ms | 6 ms |

Para rodar no ambiente dev: em Actions, escolha o workflow `k6-carga.yml` e clique em "Run workflow".
Só rode quando ninguém mais estiver mandando ordens para dev, senão a exposição não fecha. O resumo
aparece na página do run e fica como anexo. Na máquina: `k6 run -e FLOWA_URL=http://localhost:8080 "performance test/carga-ordens.js"`.

This is a challenge by [Coodesh](https://coodesh.com/)
