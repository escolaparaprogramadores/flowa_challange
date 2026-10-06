# Contrato entre as partes do Flowa — v3

Este arquivo é o acordo entre o OrderGenerator, o OrderAccumulator, a tela e o `docker compose`.
Quem implementa segue o que está aqui. Mudou alguma coisa? Sobe a versão e avisa quem usa.
A v2 trocou o formato das respostas HTTP: sucesso em `DataMessage` e erro em `application/problem+json`.
A v3 traz o motivo da rejeição na lista, a resposta `Rejected` para toda ordem que o OrderAccumulator
não consegue decidir, o `422` e o `503` de sessão perdida no envio, os dois prazos configuráveis, o
OrderGenerator lendo a lista e a exposição direto no banco e o OrderAccumulator sem HTTP, só com o FIX.

A regra de campo da ordem (símbolos `PETR4`/`VALE3`/`VIIA4`, lado, quantidade inteira maior que zero e
menor que 100.000, preço maior que zero, menor que 1.000 e múltiplo de 0,01) e as mensagens dela moram
só no OrderAccumulator, em `Flowa.OrderAccumulator.Domain.Orders.ValueObjects` (`OrderFieldPolicy`,
`OrderFieldMessages`).
O OrderGenerator não aplica essa regra: confere só o formato (o que nem cabe numa `NewOrderSingle`) e
manda o resto pelo FIX. A tela guarda uma cópia da regra só para avisar antes de enviar.

## 1. Rotas HTTP

JSON com nomes de campo em inglês, no formato camelCase. Mensagens para quem usa, em português.
Números (quantidade, preço, exposição) vão como número JSON, com ponto como separador decimal.

### Formato de toda resposta das rotas `/api/*`

**Sucesso** sai com `Content-Type: application/json` neste envelope (`DataMessage`):

```json
{ "success": true, "status": "Ok", "message": "Ordem aceita.", "data": { … }, "errors": [], "errorCode": null }
```

- `data` é o corpo da rota, descrito em cada seção abaixo.
- `message` é o texto em português que a tela pode mostrar.
- `status` vem pelo nome (`"Ok"`), nunca por número.

**Erro** sai com `Content-Type: application/problem+json` (RFC 9457), para qualquer cliente, inclusive
quem pede `text/html`:

```json
{
  "type": "urn:base-investimentos:problem:fix-session-not-logged-on",
  "title": "Serviço indisponível",
  "status": 503,
  "detail": "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.",
  "instance": "/api/orders",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "success": false,
  "statusResultado": "ServiceUnavailable",
  "errors": []
}
```

| Campo | Regra |
|---|---|
| `type` | `urn:base-investimentos:problem:<código>`. O código é fixo e é por ele que o cliente decide, nunca pelo texto. |
| `title` | Fixo por tipo de erro, em português. |
| `status` | O mesmo número do status HTTP. |
| `detail` | Mensagem em português desta ocorrência. Nunca leva exceção, SQL, host ou stack trace. |
| `instance` | Caminho pedido. |
| `traceId` | 32 caracteres hexadecimais: o trace da requisição no Datadog, o mesmo da linha de log do erro. Num erro de `POST /api/orders` depois de a ordem ter `ClOrdID` (422, 503 e 500 da tabela da ordem), é o próprio `ClOrdID`. Sem o tracer ligado (teste local), é o `ClOrdID` no erro da ordem e o trace id da requisição no ASP.NET nos outros. |
| `success` | Sempre `false`. |
| `statusResultado` | Nome do resultado: `InvalidInput` (400), `NotFound` (404), `BusinessRuleViolated` (422), `ServiceUnavailable` (503), `InternalError` (500). |
| `errors` | Lista de mensagens em português (no 400 de formato, uma por campo). |

Códigos de `type` usados hoje:

| Código | HTTP | Quando |
|---|---|---|
| `invalid-order` | 400 | `POST /api/orders` com ordem que não cabe no FIX |
| `invalid-input` | 400 | pedido que o servidor não consegue ler; `detail` = `"Dados inválidos"` |
| `invalid-page` | 400 | `GET /api/orders` com página inválida |
| `not-found` | 404 | caminho `/api/...` que não existe |
| `method-not-allowed` | 405 | verbo que a rota `/api/...` não aceita |
| `fix-order-rejected` | 422 | `POST /api/orders` recusado pela sessão FIX (`35=3`) ou pelo OrderAccumulator (`35=j`): a ordem não entrou |
| `fix-session-not-logged-on` | 503 | `POST /api/orders` sem sessão FIX logada |
| `execution-report-timeout` | 503 | `POST /api/orders` sem `ExecutionReport` no prazo (`Fix:ExecutionReportTimeoutSeconds`, padrão 5 s) |
| `fix-session-lost` | 503 | `POST /api/orders` com a sessão FIX caindo enquanto a ordem esperava a resposta |
| `internal-error` | 500 | erro inesperado, inclusive banco fora do ar na lista, na exposição e no "apagar tudo"; `detail` = `"Aconteceu um erro inesperado. Informe o traceId ao suporte."` |

Todo erro HTTP deixa exatamente uma linha de log (Warning para o erro esperado, Error com a exceção para o
inesperado), com o mesmo trace id do `traceId`, o `type` no campo `ErrorCode`, o verbo e o modelo da rota.

### OrderGenerator — `POST /api/orders`

Recebe a ordem da tela, confere o formato, manda a `NewOrderSingle` e devolve o que o
OrderAccumulator respondeu.

Pedido:

```json
{ "symbol": "PETR4", "side": "buy", "quantity": 100, "price": 10.50 }
```

- `side` é `"buy"` (Compra) ou `"sell"` (Venda).
- O servidor lê `quantity` e `price` como texto cru (número ou string JSON) e confere o formato desse
  texto. Assim, `"abc"` na quantidade volta com a mensagem certa, e não com um erro genérico do
  framework. `1.5` na quantidade cabe no FIX: vai para o OrderAccumulator, que a rejeita.
- O serviço da tela tem um modo de teste (`mode: 'test'` em `frontend/src/services/ordersService.ts`): nele
  `symbol`, `quantity` e `price` vão como texto JSON, do jeito que foram digitados, só com a vírgula decimal
  trocada por ponto (`"1,5"` vira `"1.5"`). Quem confere o formato é o servidor, como acima. Fora do modo de
  teste, `quantity` e `price` vão como número.
- O `ClOrdID` é gerado pelo OrderGenerator: é o trace id de 128 bits do rastro do pedido (32 caracteres
  hexadecimais). Sem tracer ligado, é um identificador aleatório de 32 caracteres. A tela não manda.

Respostas:

| Situação | HTTP | Corpo |
|---|---|---|
| Ordem aceita (`150=0`) | 200 | `DataMessage` com `message: "Ordem aceita."` e `data: { "status": "accepted", "clOrdId", "orderId", "execId", "symbol", "side", "quantity", "price" }` |
| Ordem rejeitada (`150=8`): por limite, por campo fora da regra, por `ClOrdID` repetido com outros dados, ou porque o OrderAccumulator não conseguiu decidir a tempo | 200 | `DataMessage` com `message: "<texto da tag 58>"` e `data: { "status": "rejected", … }` (mesmos campos) |
| Ordem que não cabe no FIX (nada é enviado por FIX) | 400 | problem `invalid-order`, `detail: "A ordem tem campos inválidos."`, `errors: ["Informe o preço."]` |
| Recusa da sessão FIX (`35=3`) ou do OrderAccumulator (`35=j`) | 422 | problem `fix-order-rejected`, title `"Regra de negócio violada"`, `detail` = texto da tag 58 da recusa, `traceId` = `ClOrdID`. Sai na hora, sem esperar o prazo |
| Sem sessão FIX | 503 | problem `fix-session-not-logged-on`, `detail: "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes."`, `traceId` = `ClOrdID` |
| Sem resposta no prazo | 503 | problem `execution-report-timeout`, `detail: "A ordem pode ter sido aceita. Confira a lista antes de enviar de novo."`, `traceId` = `ClOrdID` |
| Sessão FIX caiu com a ordem esperando resposta | 503 | problem `fix-session-lost`, mesmo `detail` do prazo, `traceId` = `ClOrdID`. Sai na hora da queda |
| `ExecutionReport` fora do contrato | 500 | problem `internal-error` (sem stack trace), `traceId` = `ClOrdID` |
| Erro inesperado antes de a ordem sair | 500 | problem `internal-error` (sem stack trace) |

- O `400` só sai quando a ordem não cabe no FIX: campo faltando (`"Informe o símbolo."`,
  `"Informe o lado da ordem."`, `"Informe a quantidade."`, `"Informe o preço."`), lado diferente de
  `"buy"`/`"sell"` (`"Lado inválido. Use compra ou venda."`), quantidade que não é número
  (`"A quantidade deve ser um número inteiro."`), preço que não é número (`"O preço deve ser um número."`)
  e símbolo com caractere de controle (`"O símbolo não pode ter caractere de controle."`). Número com mais
  dígitos do que o decimal do FIX guarda conta como "não é número". Corpo que não é JSON volta com as
  quatro mensagens de campo faltando.
- `errors` traz no máximo uma mensagem por campo, na ordem `symbol`, `side`, `quantity`, `price`.
- Campo que cabe no FIX, mas está fora da regra (símbolo `ITUB4`, quantidade `0`, `1.5` ou `100000`,
  preço `1000` ou `10.005`), vai pelo FIX e volta `200` com `data.status: "rejected"` e os motivos na `message`.
- Em `accepted` e `rejected`, `side` volta como `"buy"`/`"sell"` e `quantity`/`price` como número.
- No `422`, sem tag 58 na recusa o `detail` é `"A sessão FIX recusou a ordem (SessionRejectReason <373>)."`
  ou `"A sessão FIX recusou a ordem."` no `35=3`, e `"O OrderAccumulator recusou a ordem (BusinessRejectReason <380>)."`
  no `35=j`. O `35=j` é casado com a ordem pela tag 379 ou, sem ela, pela 45 (`RefSeqNum`).
- `503` por prazo ou por queda da sessão quer dizer "pode ter sido aceita": a ordem pode estar gravada.
  `ExecutionReport` que chega depois do prazo é
  descartado com um log Warning `"ExecutionReport arrived for an order that is no longer waiting for it."`.

### OrderGenerator — `GET /api/exposures`

Lê a exposição direto no PostgreSQL, na tabela que o OrderAccumulator grava, e devolve a exposição atual
dos três símbolos, sempre nesta ordem: `PETR4`, `VALE3`, `VIIA4`.
`message` é `"Exposição dos símbolos lida."` e `data` é:

```json
{
  "limit": 100000000.00,
  "exposures": [
    { "symbol": "PETR4", "exposure": 1000.00, "remaining": 99999000.00 },
    { "symbol": "VALE3", "exposure": -500.00, "remaining": 99999500.00 },
    { "symbol": "VIIA4", "exposure": 0.00, "remaining": 100000000.00 }
  ]
}
```

- `exposure` = soma de `preço × quantidade` das compras aceitas menos a das vendas aceitas.
- `remaining` = `limit - |exposure|`: quanto ainda cabe antes de estourar, para qualquer lado.
- `limit` é uma constante no código (`ExposureLimitPolicy`). Não vem de configuração nem de variável de ambiente.
- Sem a tabela (banco recém-criado), cada símbolo vem com exposição `0`.
- Banco fora do ar → `500` com o problem `internal-error`.

### OrderGenerator — `GET /api/orders?page=<n>` e `DELETE /api/orders`

As duas rotas leem e apagam direto no PostgreSQL, sem passar pelo OrderAccumulator.

`GET` lista as ordens gravadas no banco, 10 por página, da mais nova para a mais velha.
`message` é `"Página de ordens lida."` e `data` é:

```json
{
  "page": 1,
  "pageSize": 10,
  "total": 12,
  "orders": [
    { "receivedAt": "2026-10-04T12:00:00Z", "status": "accepted", "symbol": "PETR4", "side": "buy",
      "quantity": 100, "price": 10.50, "orderId": "…", "clOrdId": "…", "rejectReason": null }
  ]
}
```

- `receivedAt` é ISO-8601 em UTC. `status` é `"accepted"` ou `"rejected"`; `symbol` e `side` podem ser
  `null` numa ordem rejeitada que chegou pelo FIX com o campo fora do padrão.
- `rejectReason` é o motivo gravado da rejeição, o mesmo texto da tag 58; `null` na ordem aceita.
- O tamanho da página é fixo no servidor; `pageSize` vindo do cliente é ignorado. Sem `page`, vem a página 1.
- Página `0`, negativa, texto, repetida ou acima de `1000` → `400` com o problem `invalid-page`,
  `detail: "Página inválida."` e `errors: ["A página deve ser um número inteiro de 1 a 1000."]`.
- Página além da última → `200` com `orders: []` e o `total` real.
- Sem a tabela de ordens (banco recém-criado) → a página vazia, com `total: 0`.

`DELETE` apaga todas as ordens e zera a exposição de `PETR4`, `VALE3` e `VIIA4` numa transação só
(tudo ou nada) e responde `204` sem corpo (não há corpo para pôr no envelope). Não pede senha. Outro verbo
em `/api/orders` (fora `POST`, a ordem) não apaga nada.

- Banco fora do ar → `500` com o problem `internal-error`.
- Nenhuma das duas manda `Access-Control-Allow-Origin`: outra página não consegue chamá-las.

### OrderGenerator — `GET /health` e `GET /version`

`/health` responde `200` com o texto `Healthy` quando o processo está de pé, sem depender da sessão
FIX nem do banco. `/version` responde `200` com `{"commit":"<sha completo, 40 caracteres>"}`: o commit
do código que está rodando, gravado no build, para conferir que a versão no ar é a que foi revisada.

O OrderAccumulator não tem HTTP: só a sessão FIX na porta 9876. Ele também grava o commit no build, não
sobe sem ele, e o escreve no log `Application started.` (campo `BuildCommitSha`).

## 2. Mensagens FIX 4.4

Pacotes: `QuickFIXn.Core` e `QuickFIXn.FIX44`, versão `1.14.1` (os dois têm alvo `net10.0`).
As duas pontas validam as mensagens com o mesmo dicionário FIX 4.4, que acrescenta a tag 5100
(`TraceParent`): um arquivo só, `src/flowa.commons/Fix/FIX44-flowa.xml`, que o build põe ao lado do
executável de cada app.

### `NewOrderSingle` (`35=D`), do OrderGenerator para o OrderAccumulator

| Tag | Campo | Valor |
|---|---|---|
| 11 | ClOrdID | gerado pelo OrderGenerator, único por ordem |
| 55 | Symbol | `PETR4`, `VALE3` ou `VIIA4` |
| 54 | Side | `1` compra, `2` venda |
| 38 | OrderQty | inteiro, `0 < q < 100000` |
| 44 | Price | `0 < p < 1000`, passo de `0.01` |
| 40 | OrdType | sempre `2` (limitada) |
| 60 | TransactTime | hora UTC do envio |

### `ExecutionReport` (`35=8`), do OrderAccumulator para o OrderGenerator

| Tag | Campo | Aceita | Rejeitada |
|---|---|---|---|
| 37 | OrderID | gerado pelo OrderAccumulator | gerado também |
| 17 | ExecID | único por relatório | único por relatório |
| 11 | ClOrdID | o da ordem | o da ordem |
| 55 | Symbol | o da ordem | o da ordem |
| 54 | Side | o da ordem | o da ordem |
| 150 | ExecType | `0` (New) | `8` (Rejected) |
| 39 | OrdStatus | `0` (New) | `8` (Rejected) |
| 151 | LeavesQty | a quantidade da ordem | `0` |
| 14 | CumQty | `0` | `0` |
| 6 | AvgPx | `0` | `0` |
| 103 | OrdRejReason | não vai | `6` (Duplicate order) só no `ClOrdID` repetido com outros dados |
| 58 | Text | não vai | o motivo, em português |

Toda `NewOrderSingle` que passa pela sessão recebe um `ExecutionReport`, mesmo quando o OrderAccumulator
não consegue decidir. Sem `ClOrdID` (11), `Symbol` (55) ou `Side` (54), a própria sessão FIX recusa a
mensagem com `Reject` (`35=3`, `373=1`, `58=Required tag missing`), porque o dicionário marca esses
campos como obrigatórios. O motivo da rejeição em `58`:

- campo fora da regra (só o OrderAccumulator valida, venha a ordem da tela ou de FIX direto): as mensagens de `OrderFieldMessages` dos campos com erro, separadas por espaço,
  na ordem `symbol`, `side`, `quantity`, `price`. Uma ordem rejeitada aqui não muda a exposição.
  Sem `OrderQty` (38) ou sem `Price` (44), o valor conta como zero e volta
  `A quantidade deve ser maior que zero.` / `O preço deve ser maior que zero.`.
- limite: `Ordem rejeitada: a exposição de <SÍMBOLO> passaria do limite de 100.000.000,00.`
- falha interna (o banco caiu, por exemplo): `Ordem rejeitada: o OrderAccumulator não conseguiu decidir a ordem agora. Tente de novo.`
- prazo do banco vencido: `Ordem rejeitada: o OrderAccumulator não decidiu a ordem em <N> s. Tente de novo.`,
  com `<N>` = `Orders:DecisionTimeoutSeconds` (padrão 4).
- `ClOrdID` repetido com outro símbolo, lado, quantidade ou preço:
  `Ordem rejeitada: o ClOrdID <id> já foi usado com outros dados.`, com `103=6`.

Na falha interna, no prazo e no `ClOrdID` repetido com outros dados, `37` e `17` são novos, e `11`, `55` e
`54` repetem o que chegou. Na falha interna
e no `ClOrdID` repetido com outros dados, nada é gravado e a ordem original (se houver) não muda.

No prazo há uma exceção. Se o `COMMIT` já tinha sido enviado ao banco quando o prazo venceu, ele termina:
a ordem respondida `Rejected` pode ficar gravada como aceita e contar na exposição. O OrderAccumulator
registra esse caso num log Error com `ErrorCode` `order_accepted_after_the_deadline`. Por isso, uma ordem
rejeitada pelo prazo pode ter entrado: confira a lista antes de enviar de novo.

Ordem repetida com os mesmos dados (mesmo `ClOrdID`, símbolo, lado, quantidade e preço): o
OrderAccumulator devolve o `ExecutionReport` original que gravou (mesmos `37`, `17`, `150`, `39`, `58`)
e não conta a ordem de novo.

## 3. Sessão FIX

| Item | OrderGenerator | OrderAccumulator |
|---|---|---|
| Papel | initiator | acceptor |
| BeginString | `FIX.4.4` | `FIX.4.4` |
| SenderCompID | `ORDERGENERATOR` | `ORDERACCUMULATOR` |
| TargetCompID | `ORDERACCUMULATOR` | `ORDERGENERATOR` |
| Endereço | conecta em `Fix__AcceptorHost`:`Fix__AcceptorPort` | escuta em `Fix__AcceptorPort` |
| HeartBtInt | 30 | — (usa o do initiator) |
| ReconnectInterval | 2 s | — |

Nas duas pontas, para a sessão sobreviver à troca de container:

- `ResetOnLogon=Y`, `ResetOnLogout=Y`, `ResetOnDisconnect=Y`;
- store de mensagens em memória, nada em disco;
- log FIX no `stdout`, para aparecer no `docker compose logs`.

O OrderGenerator confere se a sessão está logada antes de enviar. Sem sessão, responde
o `503` `fix-session-not-logged-on` na hora. Com sessão, espera o `ExecutionReport` do mesmo `ClOrdID` pelo
prazo de `Fix:ExecutionReportTimeoutSeconds` (de 1 a 5 s, padrão 5; fora disso o app não sobe); passou
disso, o `503` `execution-report-timeout`. Um `Reject` (`35=3`) ou `BusinessMessageReject` (`35=j`) da
ordem encerra a espera na hora com o `422` `fix-order-rejected`, e a queda da sessão encerra na hora
todas as esperas com o `503` `fix-session-lost`. Quando o OrderAccumulator volta, o initiator reloga
sozinho.

O OrderAccumulator decide cada ordem dentro de `Orders:DecisionTimeoutSeconds` (padrão 4 s, maior que
zero; senão o app não sobe).

## 4. Portas e variáveis de ambiente

| Processo | Porta | Para quê |
|---|---|---|
| OrderGenerator | 8080 (HTTP); 8443 (HTTPS, só fora do compose) | página, `/api/*`, `/health`, `/version` |
| OrderAccumulator | 9876 (TCP) | acceptor FIX (o único canal dele; não tem HTTP) |
| PostgreSQL | 5432 | banco: o OrderAccumulator grava; o OrderGenerator lê a lista e a exposição e apaga |

As variáveis seguem o padrão do ASP.NET Core (`__` separa as seções). O valor da coluna "fora do
compose" é o padrão para rodar na máquina, sem Docker.

| Variável | App | Fora do compose | No compose |
|---|---|---|---|
| `ASPNETCORE_HTTP_PORTS` | OrderGenerator | `8080` | `8080` |
| `ASPNETCORE_HTTPS_PORTS` | OrderGenerator | `8443`, certificado de desenvolvimento do .NET (`dotnet dev-certs https`) | não usa |
| `Fix__AcceptorHost` | OrderGenerator | `localhost` | `orderaccumulator` |
| `Fix__AcceptorPort` | os dois | `9876` | `9876` |
| `Fix__ExecutionReportTimeoutSeconds` | OrderGenerator | `5` (de 1 a 5) | `5` (padrão) |
| `Orders__DecisionTimeoutSeconds` | OrderAccumulator | `4` (maior que zero) | `4` (padrão) |
| `ConnectionStrings__Flowa` | os dois | host `localhost` | host `postgres` |
| `Database__MaximumPoolSize` | os dois, opcional | padrão do Npgsql | `10` |

A string do banco tem o formato do Npgsql: `Host=<host>;Port=5432;Database=flowa;Username=flowa;`
seguido da senha. A senha nunca fica escrita no código nem neste contrato: o compose lê de
`POSTGRES_PASSWORD` e monta a string; fora do compose, quem roda define `ConnectionStrings__Flowa`
inteira, a mesma nos dois apps. O valor de desenvolvimento local fica declarado no README.

Nomes dos serviços no compose: `ordergenerator`, `orderaccumulator`, `postgres`. Para o avaliador,
basta publicar a porta `8080` do OrderGenerator; as outras podem ficar só na rede interna.

## 5. A página

- O build do Vite (fatia da tela) sai em `src/flowa.ordergenerator-webapi-ecs/wwwroot/`. Essa pasta é gerada e fica
  fora do Git.
- O OrderGenerator serve essa pasta na raiz (`/`), com `index.html` como página padrão. Caminhos que
  começam com `/api` nunca caem no `index.html`.
- Rodando o Vite em modo de desenvolvimento, ele repassa `/api` para `http://localhost:8080`.
