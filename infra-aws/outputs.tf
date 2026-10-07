# Contrato com a fatia de serviços. Ela mora no mesmo módulo raiz, então usa estes `local` direto; os
# `output` com os mesmos nomes servem à esteira e a quem lê o state.
locals {
  vpc_id                    = aws_vpc.rede_flowa.id
  subnets_tarefas           = aws_subnet.tarefas[*].id
  subnets_banco             = aws_subnet.banco[*].id
  sg_generator              = aws_security_group.generator.id
  sg_accumulator            = aws_security_group.accumulator.id
  sg_datadog_metrics        = aws_security_group.datadog_metrics.id
  sg_banco                  = aws_security_group.banco.id
  namespace_id              = aws_service_discovery_private_dns_namespace.descoberta_privada_dos_servicos_flowa.id
  db_endpoint               = aws_db_instance.banco.address
  db_secret_arn             = aws_secretsmanager_secret.banco.arn
  log_group_generator       = aws_cloudwatch_log_group.logs_dos_servicos_flowa["generator"].name
  log_group_accumulator     = aws_cloudwatch_log_group.logs_dos_servicos_flowa["accumulator"].name
  log_group_datadog_metrics = aws_cloudwatch_log_group.logs_dos_servicos_flowa["datadog_metrics"].name
  ecr_generator_url         = aws_ecr_repository.imagens_dos_servicos_flowa["generator"].repository_url
  ecr_accumulator_url       = aws_ecr_repository.imagens_dos_servicos_flowa["accumulator"].repository_url
  ecr_datadog_metrics_url   = aws_ecr_repository.imagens_dos_servicos_flowa["datadog_metrics"].repository_url
}

output "vpc_id" {
  value = local.vpc_id
}

output "subnets_tarefas" {
  description = "Subnets públicas das tasks, uma por AZ"
  value       = local.subnets_tarefas
}

output "subnets_banco" {
  description = "Subnets isoladas do RDS"
  value       = local.subnets_banco
}

output "sg_generator" {
  value = local.sg_generator
}

output "sg_accumulator" {
  value = local.sg_accumulator
}

output "sg_datadog_metrics" {
  value = local.sg_datadog_metrics
}

output "sg_banco" {
  value = local.sg_banco
}

output "namespace_id" {
  description = "Namespace DNS privado do Cloud Map"
  value       = local.namespace_id
}

# Os quatro abaixo levam o id da conta ou o endereço do banco. Ficam escondidos no log público da esteira;
# `terraform output -raw <nome>` continua devolvendo o valor.
output "db_endpoint" {
  value     = local.db_endpoint
  sensitive = true
}

output "db_secret_arn" {
  value     = local.db_secret_arn
  sensitive = true
}

output "log_group_generator" {
  value = local.log_group_generator
}

output "log_group_accumulator" {
  value = local.log_group_accumulator
}

output "log_group_datadog_metrics" {
  value = local.log_group_datadog_metrics
}

output "ecr_generator_url" {
  value     = local.ecr_generator_url
  sensitive = true
}

output "ecr_accumulator_url" {
  value     = local.ecr_accumulator_url
  sensitive = true
}

output "ecr_datadog_metrics_url" {
  value     = local.ecr_datadog_metrics_url
  sensitive = true
}
