# Flowa — envio de ordens com FIX 4.4

Dois serviços em .NET conversam por FIX: o OrderGenerator manda ordens de compra e venda e o
OrderAccumulator aceita ou rejeita cada uma conforme o limite de exposição por ativo.

This is a challenge by [Coodesh](https://coodesh.com/)

## Como rodar

Precisa só do Docker com o Compose. Na raiz do repositório:

```bash
docker compose up
```

Isso constrói as imagens, sobe o PostgreSQL, o OrderAccumulator e o OrderGenerator, e a página
fica em http://localhost:8080. Para parar, `Ctrl+C`; para apagar também os dados do banco,
`docker compose down -v`.

A senha do PostgreSQL vem da variável `POSTGRES_PASSWORD`. Sem ela, o compose usa `flowa_dev`,
um valor só para desenvolvimento local; o banco nem fica exposto fora da rede do compose.
Se a porta 8080 já estiver ocupada, troque a da página com `FLOWA_HTTP_PORT`, por exemplo
`FLOWA_HTTP_PORT=9080 docker compose up`.

Para compilar e testar o código (precisa do .NET 10 SDK):

```bash
dotnet build Flowa.sln
dotnet test Flowa.sln --filter "Category!=Integration"
```

O segundo comando roda os testes de unidade e os do OrderAccumulator contra um PostgreSQL de
teste (precisam do Docker de pé). Sem o filtro, `dotnet test Flowa.sln` roda também os testes
de integração, que sobem o compose inteiro num projeto separado (porta 18080) e conferem a
ida e volta FIX entre os dois containers e a religação depois de recriar o OrderAccumulator.
