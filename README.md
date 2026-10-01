# Flowa — envio de ordens com FIX 4.4

Dois serviços em .NET conversam por FIX: o OrderGenerator manda ordens de compra e venda e o
OrderAccumulator aceita ou rejeita cada uma conforme o limite de exposição por ativo.

This is a challenge by [Coodesh](https://coodesh.com/)

## Como rodar

Precisa só do Docker com o Compose. Na raiz do repositório:

```bash
docker compose up
```

Isso constrói as imagens e sobe o PostgreSQL, o OrderAccumulator e o OrderGenerator; a página fica em
http://localhost:8080 (só nesta máquina). `Ctrl+C` para; `docker compose down -v` apaga também o banco.
A senha do PostgreSQL vem de `POSTGRES_PASSWORD`; sem ela, o compose usa `flowa_dev`, valor só de
desenvolvimento local (o banco não sai da rede do compose). Com a 8080 ocupada, use `FLOWA_HTTP_PORT=9080`.

Para compilar e testar (precisa do .NET 10 SDK e do Docker de pé):

```bash
dotnet build Flowa.sln
dotnet test Flowa.sln --filter "Category!=Integration"
```

O filtro deixa de fora os testes de integração, que sobem o compose inteiro em projetos separados e
conferem a ida e volta FIX entre os containers e a religação depois de recriar o OrderAccumulator.
