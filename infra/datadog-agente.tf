# Agente do Datadog ao lado de cada app (CA-O3). Fica desligado até a esteira conferir que o cofre já
# tem a chave (decisão 3): sem ela, cada task continua só com o app, do tamanho da onda 2.

variable "datadog_ligado" {
  description = "Põe o agente do Datadog nas duas tasks. A esteira só passa true quando o cofre tem versão AWSCURRENT."
  type        = bool
  default     = false
}

variable "taxa_amostragem_rastros" {
  description = "Fração dos rastros que o tracer de cada app manda ao agente (1.0 = todos)."
  type        = number
  default     = 1.0

  validation {
    condition     = var.taxa_amostragem_rastros >= 0 && var.taxa_amostragem_rastros <= 1
    error_message = "taxa_amostragem_rastros vai de 0 a 1."
  }
}

locals {
  # Versão fixa: a tag :7 andaria sozinha e trocaria o agente sem merge.
  imagem_do_agente_datadog = "public.ecr.aws/datadog/agent:7.84.1"

  # Com o agente, 0,5 vCPU / 1 GB (decisão 2, custo aprovado); sem ele, o tamanho de antes.
  cpu_da_task_flowa     = var.datadog_ligado ? 512 : 256
  memoria_da_task_flowa = var.datadog_ligado ? 1024 : 512

  nome_no_datadog_por_servico_flowa = {
    generator   = "order-generator"
    accumulator = "order-accumulator"
  }

  # A versão de cada serviço é a tag da imagem dele, a mesma que o /version devolve (D-05).
  versao_no_datadog_por_servico_flowa = {
    generator   = var.generator_image_tag
    accumulator = var.accumulator_image_tag
  }

  # O tracer acha o agente em localhost:8126 e as métricas vão para localhost:8125 (UDP), os padrões
  # do Fargate; por isso não há DD_AGENT_HOST.
  variaveis_datadog_do_app_por_servico_flowa = {
    for servico_flowa, nome_no_datadog in local.nome_no_datadog_por_servico_flowa :
    servico_flowa => var.datadog_ligado ? [
      { name = "DD_ENV", value = local.ambiente_dos_recursos_flowa },
      { name = "DD_SERVICE", value = nome_no_datadog },
      { name = "DD_VERSION", value = local.versao_no_datadog_por_servico_flowa[servico_flowa] },
      { name = "DD_TRACE_SAMPLE_RATE", value = tostring(var.taxa_amostragem_rastros) },
    ] : []
  }

  containers_do_agente_por_servico_flowa = {
    for servico_flowa in keys(local.nome_no_datadog_por_servico_flowa) :
    servico_flowa => var.datadog_ligado ? [{
      name  = "datadog-agent"
      image = local.imagem_do_agente_datadog
      # Se o agente cair ou a chave estiver errada, a task segue de pé e as ordens continuam passando.
      essential = false

      environment = [
        { name = "ECS_FARGATE", value = "true" },
        { name = "DD_SITE", value = "datadoghq.com" },
        { name = "DD_ENV", value = local.ambiente_dos_recursos_flowa },
        { name = "DD_APM_ENABLED", value = "true" },
        { name = "DD_DOGSTATSD_PORT", value = "8125" },
        # O health check do ECS bate no /health a cada 15 s; esses rastros só fariam ruído.
        { name = "DD_APM_IGNORE_RESOURCES", value = "^GET /health$" },
        { name = "DD_LOGS_ENABLED", value = "false" },
        { name = "DD_LOG_LEVEL", value = "warn" },
      ]

      # O segredo é texto puro, sem campo JSON; a execution role o lê quando a task sobe.
      secrets = [
        { name = "DD_API_KEY", valueFrom = aws_secretsmanager_secret.chave_api_do_datadog.arn },
      ]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = local.log_group_por_servico_flowa[servico_flowa]
          awslogs-region        = var.region
          awslogs-stream-prefix = "datadog-agent"
        }
      }
    }] : []
  }

  permissao_de_ler_a_chave_do_datadog = var.datadog_ligado ? [{
    Effect   = "Allow"
    Action   = "secretsmanager:GetSecretValue"
    Resource = aws_secretsmanager_secret.chave_api_do_datadog.arn
  }] : []
}
