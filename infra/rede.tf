# Rede sem NAT: as tasks ficam em subnet pública e ganham IP público para puxar imagem do ECR (não há
# NAT nem endpoint de VPC, que custariam mais que o resto da rede). Quem as protege é o security group,
# que não aceita nada vindo da internet. O banco fica em subnets sem rota para fora.

data "aws_availability_zones" "disponiveis" {
  state = "available"

  filter {
    name   = "opt-in-status"
    values = ["opt-in-not-required"]
  }
}

locals {
  azs = slice(data.aws_availability_zones.disponiveis.names, 0, 2)

  cidr_vpc            = "10.40.0.0/16"
  cidrs_tarefas       = ["10.40.0.0/24", "10.40.1.0/24"]
  cidrs_banco         = ["10.40.10.0/24", "10.40.11.0/24"]
  sufixos_das_subnets = ["a", "b"]
}

resource "aws_vpc" "principal" {
  cidr_block = local.cidr_vpc

  # O namespace privado do Cloud Map só resolve com as duas ligadas.
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = "${local.prefixo}-vpc" }
}

# O security group padrão da VPC aceita tudo que vem dele mesmo. Fica sem regra nenhuma, para ninguém
# cair nele por engano.
resource "aws_default_security_group" "padrao" {
  vpc_id = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-padrao-sem-regras" }
}

resource "aws_internet_gateway" "principal" {
  vpc_id = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-igw" }
}

resource "aws_subnet" "tarefas" {
  count = 2

  vpc_id            = aws_vpc.principal.id
  availability_zone = local.azs[count.index]
  cidr_block        = local.cidrs_tarefas[count.index]

  # O IP público é pedido pela task (assign_public_ip no serviço), não dado a tudo que nascer aqui.
  map_public_ip_on_launch = false

  tags = { Name = "${local.prefixo}-tarefas-${local.sufixos_das_subnets[count.index]}" }
}

resource "aws_subnet" "banco" {
  count = 2

  vpc_id            = aws_vpc.principal.id
  availability_zone = local.azs[count.index]
  cidr_block        = local.cidrs_banco[count.index]

  map_public_ip_on_launch = false

  tags = { Name = "${local.prefixo}-banco-${local.sufixos_das_subnets[count.index]}" }
}

resource "aws_route_table" "tarefas" {
  vpc_id = aws_vpc.principal.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.principal.id
  }

  tags = { Name = "${local.prefixo}-tarefas" }
}

resource "aws_route_table_association" "tarefas" {
  count = 2

  subnet_id      = aws_subnet.tarefas[count.index].id
  route_table_id = aws_route_table.tarefas.id
}

# Sem bloco `route`: só a rota local da VPC. É isso que faz a subnet do banco ser isolada.
resource "aws_route_table" "banco" {
  vpc_id = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-banco-isolada" }
}

resource "aws_route_table_association" "banco" {
  count = 2

  subnet_id      = aws_subnet.banco[count.index].id
  route_table_id = aws_route_table.banco.id
}

# Security groups. Nenhuma regra de entrada vem de 0.0.0.0/0. A entrada do generator vinda do VPC Link
# é da fatia de serviços (infra/borda.tf).

resource "aws_security_group" "generator" {
  name        = "${local.prefixo}-generator"
  description = "OrderGenerator: sem entrada da internet."
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-generator" }
}

resource "aws_security_group" "accumulator" {
  name        = "${local.prefixo}-accumulator"
  description = "OrderAccumulator: FIX e HTTP so a partir do generator."
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-accumulator" }
}

resource "aws_security_group" "banco" {
  name        = "${local.prefixo}-banco"
  description = "PostgreSQL: 5432 so a partir do accumulator."
  vpc_id      = aws_vpc.principal.id

  tags = { Name = "${local.prefixo}-banco" }
}

resource "aws_vpc_security_group_ingress_rule" "accumulator_fix" {
  security_group_id            = aws_security_group.accumulator.id
  description                  = "FIX vindo do generator"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_fix
  to_port                      = local.porta_fix
  referenced_security_group_id = aws_security_group.generator.id
}

resource "aws_vpc_security_group_ingress_rule" "accumulator_http" {
  security_group_id            = aws_security_group.accumulator.id
  description                  = "GET /api/exposures repassado pelo generator"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_http
  to_port                      = local.porta_http
  referenced_security_group_id = aws_security_group.generator.id
}

resource "aws_vpc_security_group_ingress_rule" "banco_postgres" {
  security_group_id            = aws_security_group.banco.id
  description                  = "PostgreSQL vindo do accumulator"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.accumulator.id
}

# Saída livre só nas tasks: puxar imagem do ECR e ler o segredo passam pela internet (não há endpoint
# de VPC), e o generator fala com o accumulator. O banco não tem regra de saída.
resource "aws_vpc_security_group_egress_rule" "generator_saida" {
  security_group_id = aws_security_group.generator.id
  description       = "Saida das tasks do generator"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "accumulator_saida" {
  security_group_id = aws_security_group.accumulator.id
  description       = "Saida das tasks do accumulator"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

# Namespace DNS privado para o generator achar o accumulator pelo nome. Os serviços (e o TTL curto)
# são registrados pela fatia de serviços.
resource "aws_service_discovery_private_dns_namespace" "principal" {
  name        = "${local.prefixo}.local"
  description = "Descoberta interna do Flowa"
  vpc         = aws_vpc.principal.id
}
