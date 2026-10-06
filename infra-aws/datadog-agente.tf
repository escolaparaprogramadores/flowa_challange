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

      environment = concat([
        { name = "ECS_FARGATE", value = "true" },
        { name = "DD_SITE", value = "datadoghq.com" },
        { name = "DD_ENV", value = local.ambiente_dos_recursos_flowa },
        { name = "DD_APM_ENABLED", value = "true" },
        { name = "DD_DOGSTATSD_PORT", value = "8125" },
        ],
        # O health check do ECS bate no /health do generator a cada 15 s; esses rastros só fariam ruído.
        # O accumulator é worker sem HTTP e sem checagem de saúde: não tem esse filtro.
        servico_flowa == "generator" ? [{ name = "DD_APM_IGNORE_RESOURCES", value = "^GET /health$" }] : [],
        [
          { name = "DD_LOGS_ENABLED", value = "false" },
          { name = "DD_LOG_LEVEL", value = "warn" },
      ])

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

  imagem_do_coletor_de_logs           = "public.ecr.aws/aws-observability/aws-for-fluent-bit:3.4.17"
  caminho_da_configuracao_do_coletor  = "/tmp/flowa-coletor.conf"
  limite_de_eventos_no_driver_do_app  = "1048576"
  orcamento_de_linhas_por_hora_do_app = 2000

  # Filters run before both outputs: FIX heartbeat (35=0) and Debug/Trace are dropped, then each task keeps
  # at most 2000 lines/h averaged over a 24 h sliding window (48k a day; the count resets when the task restarts).
  configuracao_do_coletor_por_servico_flowa = {
    for servico_flowa, nome_no_datadog in local.nome_no_datadog_por_servico_flowa :
    servico_flowa => <<-CONF
      [FILTER]
          Name    grep
          Match   *-firelens-*
          Exclude log (^|[\x01|"\s]|\\u0001|\^A)35=0($|[\x01|"\s]|\\u0001|\^A)

      [FILTER]
          Name    grep
          Match   *-firelens-*
          Exclude log (^|\s)(dbug|trce):\s|"(LogLevel|level|Level|@l)"\s*:\s*"(Debug|Trace|debug|trace|Verbose)"

      [FILTER]
          Name         parser
          Match        *-firelens-*
          Key_Name     log
          Parser       json
          Reserve_Data On

      # The .NET JSON console writes Message, LogLevel and the trace id (State or Scopes) under names Datadog does
      # not read: rename them so search by clOrdId and the log-to-trace link work. Non-JSON lines keep their text.
      [FILTER]
          Name  lua
          Match *-firelens-*
          call  prepare_app_record
          code  function prepare_app_record(tag, timestamp, record) if record["Message"] ~= nil then record["message"] = record["Message"]; record["Message"] = nil elseif record["log"] ~= nil then record["message"] = record["log"]; record["log"] = nil end if record["LogLevel"] ~= nil then record["level"] = record["LogLevel"]; record["LogLevel"] = nil end local trace_id = type(record["State"]) == "table" and record["State"]["TraceId"] or nil local span_id = nil if type(record["Scopes"]) == "table" then for _, scope in ipairs(record["Scopes"]) do if type(scope) == "table" then trace_id = trace_id or scope["dd_trace_id"] or scope["TraceId"]; span_id = span_id or scope["dd_span_id"] end end end if trace_id ~= nil then record["dd"] = { trace_id = trace_id, span_id = span_id } end return 2, timestamp, record end

      [FILTER]
          Name         throttle
          Match        *-firelens-*
          Rate         ${local.orcamento_de_linhas_por_hora_do_app}
          Window       24
          Interval     1h
          Print_Status false

      [OUTPUT]
          Name           datadog
          Match          *-firelens-*
          Host           http-intake.logs.datadoghq.com
          TLS            on
          compress       gzip
          apikey         $${DD_API_KEY}
          dd_service     ${nome_no_datadog}
          dd_source      csharp
          dd_tags        env:${local.ambiente_dos_recursos_flowa},version:${local.versao_no_datadog_por_servico_flowa[servico_flowa]}
          provider       ecs

      [OUTPUT]
          Name              cloudwatch_logs
          Match             *-firelens-*
          region            ${var.region}
          log_group_name    ${local.log_group_por_servico_flowa[servico_flowa]}
          log_stream_prefix app/
          auto_create_group false
    CONF
  }

  containers_do_coletor_por_servico_flowa = {
    for servico_flowa in keys(local.nome_no_datadog_por_servico_flowa) :
    servico_flowa => var.datadog_ligado ? [{
      name  = "log-router"
      image = local.imagem_do_coletor_de_logs
      # If the collector dies, orders keep flowing and only the logs are lost.
      essential = false
      # FireLens sets each input's Mem_Buf_Limit to half of the reservation; the hard limit caps the rest.
      memoryReservation = 50
      memory            = 128

      firelensConfiguration = {
        type = "fluentbit"
        options = {
          "config-file-type"        = "file"
          "config-file-value"       = local.caminho_da_configuracao_do_coletor
          "enable-ecs-log-metadata" = "false"
        }
      }

      # No image, S3 object or volume of our own: the config travels in the task definition and is written at start.
      environment = [
        { name = "CONFIGURACAO_DO_COLETOR", value = local.configuracao_do_coletor_por_servico_flowa[servico_flowa] },
      ]
      command = ["sh", "-c", "printf '%s\\n' \"$CONFIGURACAO_DO_COLETOR\" > ${local.caminho_da_configuracao_do_coletor} && exec /fluent-bit/bin/fluent-bit -c /fluent-bit/etc/fluent-bit.conf -R /fluent-bit/etc/parsers.conf"]

      secrets = [
        { name = "DD_API_KEY", valueFrom = aws_secretsmanager_secret.chave_api_do_datadog.arn },
      ]

      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = local.log_group_por_servico_flowa[servico_flowa]
          awslogs-region        = var.region
          awslogs-stream-prefix = "log-router"
          mode                  = "non-blocking"
        }
      }
    }] : []
  }

  # No `mode` on awsfirelens: the ECS agent passes every option except the buffer limit on to the Fluent Bit
  # output, and Fluent Bit rejects `mode`. The agent already writes to the collector asynchronously.
  configuracao_de_log_do_app_por_servico_flowa = {
    for servico_flowa in keys(local.nome_no_datadog_por_servico_flowa) :
    servico_flowa => var.datadog_ligado ? {
      logDriver = "awsfirelens"
      options = tomap({
        log-driver-buffer-limit = local.limite_de_eventos_no_driver_do_app
      })
      } : {
      logDriver = "awslogs"
      options = tomap({
        awslogs-group         = local.log_group_por_servico_flowa[servico_flowa]
        awslogs-region        = var.region
        awslogs-stream-prefix = "app"
      })
    }
  }

  permissao_de_ler_a_chave_do_datadog = var.datadog_ligado ? [{
    Effect   = "Allow"
    Action   = "secretsmanager:GetSecretValue"
    Resource = aws_secretsmanager_secret.chave_api_do_datadog.arn
  }] : []
}
