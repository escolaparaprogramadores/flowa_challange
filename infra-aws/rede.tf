# Rede sem NAT: as tasks ficam em subnet pública e ganham IP público para puxar imagem do ECR (não há
# NAT nem endpoint de VPC, que custariam mais que o resto da rede). Quem as protege é o security group,
# que não aceita nada vindo da internet. O banco fica em subnets sem rota para fora.

data "aws_availability_zones" "zonas_disponiveis_para_a_rede_flowa" {
  state = "available"

  filter {
    name   = "opt-in-status"
    values = ["opt-in-not-required"]
  }
}

locals {
  zonas_de_disponibilidade_da_rede_flowa = slice(data.aws_availability_zones.zonas_disponiveis_para_a_rede_flowa.names, 0, 2)

  cidr_vpc            = "10.40.0.0/16"
  cidrs_tarefas       = ["10.40.0.0/24", "10.40.1.0/24"]
  cidrs_banco         = ["10.40.10.0/24", "10.40.11.0/24"]
  sufixos_das_subnets = ["a", "b"]
}

resource "aws_vpc" "rede_flowa" {
  cidr_block = local.cidr_vpc

  # O namespace privado do Cloud Map só resolve com as duas ligadas.
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-vpc" }
}

# O security group padrão da VPC aceita tudo que vem dele mesmo. Fica sem regra nenhuma, para ninguém
# cair nele por engano.
resource "aws_default_security_group" "padrao_da_rede_flowa_sem_regras" {
  vpc_id = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-padrao-sem-regras" }
}

resource "aws_internet_gateway" "saida_da_rede_flowa_para_internet" {
  vpc_id = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-igw" }
}

resource "aws_subnet" "tarefas" {
  count = 2

  vpc_id            = aws_vpc.rede_flowa.id
  availability_zone = local.zonas_de_disponibilidade_da_rede_flowa[count.index]
  cidr_block        = local.cidrs_tarefas[count.index]

  # O IP público é pedido pela task (assign_public_ip no serviço), não dado a tudo que nascer aqui.
  map_public_ip_on_launch = false

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-tarefas-${local.sufixos_das_subnets[count.index]}" }
}

resource "aws_subnet" "banco" {
  count = 2

  vpc_id            = aws_vpc.rede_flowa.id
  availability_zone = local.zonas_de_disponibilidade_da_rede_flowa[count.index]
  cidr_block        = local.cidrs_banco[count.index]

  map_public_ip_on_launch = false

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-banco-${local.sufixos_das_subnets[count.index]}" }
}

resource "aws_route_table" "tarefas" {
  vpc_id = aws_vpc.rede_flowa.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.saida_da_rede_flowa_para_internet.id
  }

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-tarefas" }
}

resource "aws_route_table_association" "tarefas" {
  count = 2

  subnet_id      = aws_subnet.tarefas[count.index].id
  route_table_id = aws_route_table.tarefas.id
}

# Sem bloco `route`: só a rota local da VPC. É isso que faz a subnet do banco ser isolada.
resource "aws_route_table" "banco" {
  vpc_id = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-banco-isolada" }
}

resource "aws_route_table_association" "banco" {
  count = 2

  subnet_id      = aws_subnet.banco[count.index].id
  route_table_id = aws_route_table.banco.id
}

# Security groups. Nenhuma regra de entrada vem de 0.0.0.0/0. A entrada do generator vinda do VPC Link
# é da fatia de serviços (infra-aws/borda.tf).

resource "aws_security_group" "generator" {
  name        = "${local.prefixo_dos_recursos_flowa}-generator"
  description = "OrderGenerator: sem entrada da internet."
  vpc_id      = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-generator" }
}

resource "aws_security_group" "accumulator" {
  name        = "${local.prefixo_dos_recursos_flowa}-accumulator"
  description = "OrderAccumulator: FIX e HTTP so a partir do generator."
  vpc_id      = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-accumulator" }
}

resource "aws_security_group" "datadog_metrics" {
  name        = "${local.prefixo_dos_recursos_flowa}-datadog-metrics"
  description = "Worker de metricas do Datadog: sem entrada; sai so para o banco e HTTPS."
  vpc_id      = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-datadog-metrics" }
}

resource "aws_security_group" "banco" {
  name        = "${local.prefixo_dos_recursos_flowa}-banco"
  description = "PostgreSQL: 5432 so a partir do accumulator."
  vpc_id      = aws_vpc.rede_flowa.id

  tags = { Name = "${local.prefixo_dos_recursos_flowa}-banco" }
}

resource "aws_vpc_security_group_ingress_rule" "accumulator_fix" {
  security_group_id            = aws_security_group.accumulator.id
  description                  = "FIX vindo do generator"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_fix
  to_port                      = local.porta_fix
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

resource "aws_vpc_security_group_ingress_rule" "banco_postgres_do_generator" {
  security_group_id            = aws_security_group.banco.id
  description                  = "PostgreSQL vindo do generator"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.generator.id
}

resource "aws_vpc_security_group_ingress_rule" "banco_postgres_do_datadog_metrics" {
  security_group_id            = aws_security_group.banco.id
  description                  = "PostgreSQL vindo do worker de metricas"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.datadog_metrics.id
}

# Saída só do necessário. HTTPS para fora: puxar imagem do ECR, ler o segredo e mandar log passam pela
# internet (não há endpoint de VPC). Dentro da VPC, cada um fala só com o vizinho. O banco não tem saída.
# DNS e horário usam o resolvedor da VPC, que o security group não filtra.
resource "aws_vpc_security_group_egress_rule" "generator_https" {
  security_group_id = aws_security_group.generator.id
  description       = "HTTPS do generator (ECR, segredos, logs)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "generator_fix" {
  security_group_id            = aws_security_group.generator.id
  description                  = "FIX do generator para o accumulator"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_fix
  to_port                      = local.porta_fix
  referenced_security_group_id = aws_security_group.accumulator.id
}

resource "aws_vpc_security_group_egress_rule" "generator_postgres" {
  security_group_id            = aws_security_group.generator.id
  description                  = "PostgreSQL do generator para o banco"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.banco.id
}

resource "aws_vpc_security_group_egress_rule" "accumulator_https" {
  security_group_id = aws_security_group.accumulator.id
  description       = "HTTPS do accumulator (ECR, segredos, logs)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "accumulator_postgres" {
  security_group_id            = aws_security_group.accumulator.id
  description                  = "PostgreSQL do accumulator para o banco"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.banco.id
}

resource "aws_vpc_security_group_egress_rule" "datadog_metrics_https" {
  security_group_id = aws_security_group.datadog_metrics.id
  description       = "HTTPS do worker de metricas (ECR, segredos, logs, Datadog)"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "datadog_metrics_postgres" {
  security_group_id            = aws_security_group.datadog_metrics.id
  description                  = "PostgreSQL do worker de metricas para o banco"
  ip_protocol                  = "tcp"
  from_port                    = local.banco_porta
  to_port                      = local.banco_porta
  referenced_security_group_id = aws_security_group.banco.id
}

# Namespace DNS privado para o generator achar o accumulator pelo nome. Os serviços (e o TTL curto)
# são registrados pela fatia de serviços.
resource "aws_service_discovery_private_dns_namespace" "descoberta_privada_dos_servicos_flowa" {
  name        = "${local.prefixo_dos_recursos_flowa}.local"
  description = "Descoberta interna do Flowa"
  vpc         = aws_vpc.rede_flowa.id
}
