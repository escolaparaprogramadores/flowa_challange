# A única porta de entrada pública: HTTP API do API Gateway -> VPC Link -> Cloud Map -> generator na 8080.
# Sem ALB, NLB nem NAT. O accumulator (9876 e 8081) e o banco não têm caminho vindo de fora.

variable "limite_requisicoes_por_segundo" {
  description = "Throttling do stage (R-02): requisições por segundo, em média. O teste de carga ajusta aqui."
  type        = number
  default     = 20
}

variable "limite_rajada_de_requisicoes" {
  description = "Throttling do stage (R-02): tamanho máximo da rajada."
  type        = number
  default     = 40
}

# O VPC Link só manda tráfego para o generator, e só na porta da página e da API.
resource "aws_security_group" "vpc_link" {
  name        = "${local.prefixo}-vpc-link"
  description = "VPC Link do HTTP API: sai so para o generator na 8080."
  vpc_id      = local.vpc_id

  tags = { Name = "${local.prefixo}-vpc-link" }
}

resource "aws_vpc_security_group_egress_rule" "vpc_link_para_generator" {
  security_group_id            = aws_security_group.vpc_link.id
  description                  = "Pagina e API do generator"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_generator
  to_port                      = local.porta_generator
  referenced_security_group_id = local.sg_generator
}

# A única regra de entrada do generator.
resource "aws_vpc_security_group_ingress_rule" "generator_do_vpc_link" {
  security_group_id            = local.sg_generator
  description                  = "Pagina e API vindas do VPC Link"
  ip_protocol                  = "tcp"
  from_port                    = local.porta_generator
  to_port                      = local.porta_generator
  referenced_security_group_id = aws_security_group.vpc_link.id
}

resource "aws_apigatewayv2_vpc_link" "generator" {
  name               = "${local.prefixo}-vpc-link"
  subnet_ids         = local.subnets_tarefas
  security_group_ids = [aws_security_group.vpc_link.id]
}

resource "aws_apigatewayv2_api" "publica" {
  name          = "${local.prefixo}-api"
  protocol_type = "HTTP"
  description   = "Entrada publica do Flowa: pagina e /api do OrderGenerator"
}

# A integração consulta o Cloud Map (registro SRV) e manda a requisição para o IP e a porta do generator.
resource "aws_apigatewayv2_integration" "generator" {
  api_id             = aws_apigatewayv2_api.publica.id
  integration_type   = "HTTP_PROXY"
  integration_method = "ANY"
  connection_type    = "VPC_LINK"
  connection_id      = aws_apigatewayv2_vpc_link.generator.id
  integration_uri    = aws_service_discovery_service.generator.arn

  # O generator espera até 5 s pelo ExecutionReport; 10 s cobre essa espera com folga.
  timeout_milliseconds = 10000
}

resource "aws_apigatewayv2_route" "tudo_para_o_generator" {
  api_id    = aws_apigatewayv2_api.publica.id
  route_key = "$default"
  target    = "integrations/${aws_apigatewayv2_integration.generator.id}"
}

# Throttling no stage inteiro (R-02): acima do limite o API Gateway devolve 429 sem acordar a task.
resource "aws_apigatewayv2_stage" "padrao" {
  api_id      = aws_apigatewayv2_api.publica.id
  name        = "$default"
  auto_deploy = true

  default_route_settings {
    throttling_rate_limit  = var.limite_requisicoes_por_segundo
    throttling_burst_limit = var.limite_rajada_de_requisicoes
  }
}

output "url_publica" {
  description = "Endereço público do Flowa (página e API)."
  value       = aws_apigatewayv2_stage.padrao.invoke_url
}
