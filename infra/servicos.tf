# Os dois apps em ECS Fargate: uma cópia de cada, ARM64, a menor task. Lê da fatia de rede os locals de
# infra/outputs.tf (subnets, SGs, namespace, segredo, ECR e log groups).

locals {
  # Mesmo path e mesma boundary que a stack de acesso exige da esteira (infra-base, flowa-challenge-acesso).
  path_das_roles_dos_servicos_flowa = "/${local.prefixo_dos_recursos_flowa}-app/"
  nome_da_boundary_das_roles_flowa  = "${local.prefixo_dos_recursos_flowa}-boundary"

  porta_http_do_generator = 8080

  # O generator acha o accumulator por este nome no Cloud Map (registro A, TTL curto).
  nome_dns_do_accumulator = "${aws_service_discovery_service.registro_dns_do_order_accumulator.name}.${aws_service_discovery_private_dns_namespace.descoberta_privada_dos_servicos_flowa.name}"

  # A imagem não tem curl nem wget; o bash abre o socket e confere se o /health respondeu 200.
  comando_health_check_por_servico = {
    for servico, porta_http_do_servico in { generator = local.porta_http_do_generator, accumulator = local.porta_http_do_accumulator } :
    servico => "exec 3<>/dev/tcp/127.0.0.1/${porta_http_do_servico} && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"
  }

  log_group_por_servico = {
    generator   = local.log_group_generator
    accumulator = local.log_group_accumulator
  }
}

data "aws_caller_identity" "conta_do_flowa" {}

data "aws_partition" "particao_da_conta" {}

# Pelo ARN montado com o nome, e não pelo argumento `name`: a busca por nome lista todas as policies da conta
# (iam:ListPolicies), e a esteira só pode ler a boundary.
data "aws_iam_policy" "boundary_das_roles_do_app" {
  arn = "arn:${data.aws_partition.particao_da_conta.partition}:iam::${data.aws_caller_identity.conta_do_flowa.account_id}:policy/${local.nome_da_boundary_das_roles_flowa}"
}

resource "aws_ecs_cluster" "cluster_dos_servicos_flowa" {
  name = local.prefixo_dos_recursos_flowa

  # Container Insights cobra por métrica; o desafio fica com os logs.
  setting {
    name  = "containerInsights"
    value = "disabled"
  }
}

# Execution role: é o ECS quem a usa para puxar a imagem, escrever o log e ler o segredo. Os apps não
# chamam a AWS, então não há task role.
resource "aws_iam_role" "role_de_execucao_das_tasks" {
  for_each = local.nomes_dos_servicos_flowa

  name                 = "${each.value}-execucao"
  path                 = local.path_das_roles_dos_servicos_flowa
  permissions_boundary = data.aws_iam_policy.boundary_das_roles_do_app.arn

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

# Só policy inline: a esteira não cria policy gerenciada nem anexa as da AWS.
resource "aws_iam_role_policy" "permissoes_de_execucao_das_tasks" {
  for_each = local.nomes_dos_servicos_flowa

  name = "${each.value}-execucao"
  role = aws_iam_role.role_de_execucao_das_tasks[each.key].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        {
          Effect   = "Allow"
          Action   = "ecr:GetAuthorizationToken"
          Resource = "*"
        },
        {
          Effect   = "Allow"
          Action   = ["ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability"]
          Resource = aws_ecr_repository.imagens_dos_servicos_flowa[each.key].arn
        },
        {
          Effect   = "Allow"
          Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
          Resource = "${aws_cloudwatch_log_group.logs_dos_servicos_flowa[each.key].arn}:*"
        },
      ],
      each.key == "accumulator" ? [{
        Effect   = "Allow"
        Action   = "secretsmanager:GetSecretValue"
        Resource = local.db_secret_arn
      }] : []
    )
  })
}

resource "aws_ecs_task_definition" "tarefa_do_order_generator" {
  family                   = local.nomes_dos_servicos_flowa.generator
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.role_de_execucao_das_tasks["generator"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([{
    name      = "generator"
    image     = "${local.ecr_generator_url}:${var.image_tag}"
    essential = true

    portMappings = [{ containerPort = local.porta_http_do_generator, protocol = "tcp" }]

    environment = [
      { name = "ASPNETCORE_HTTP_PORTS", value = tostring(local.porta_http_do_generator) },
      { name = "Fix__AcceptorHost", value = local.nome_dns_do_accumulator },
      { name = "Fix__AcceptorPort", value = tostring(local.porta_fix) },
      { name = "OrderAccumulator__BaseUrl", value = "http://${local.nome_dns_do_accumulator}:${local.porta_http_do_accumulator}" },
    ]

    healthCheck = {
      command     = ["CMD", "bash", "-c", local.comando_health_check_por_servico.generator]
      interval    = 15
      timeout     = 5
      retries     = 3
      startPeriod = 30
    }

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = local.log_group_por_servico.generator
        awslogs-region        = var.region
        awslogs-stream-prefix = "app"
      }
    }
  }])
}

resource "aws_ecs_task_definition" "tarefa_do_order_accumulator" {
  family                   = local.nomes_dos_servicos_flowa.accumulator
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.role_de_execucao_das_tasks["accumulator"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([{
    name      = "accumulator"
    image     = "${local.ecr_accumulator_url}:${var.image_tag}"
    essential = true

    portMappings = [
      { containerPort = local.porta_http_do_accumulator, protocol = "tcp" },
      { containerPort = local.porta_fix, protocol = "tcp" },
    ]

    environment = [
      { name = "ASPNETCORE_HTTP_PORTS", value = tostring(local.porta_http_do_accumulator) },
      { name = "Fix__AcceptorPort", value = tostring(local.porta_fix) },
    ]

    # A connection string (com a senha) vem do segredo no momento em que a task sobe; nunca fica na task
    # definition nem no state desta parte.
    secrets = [
      { name = "ConnectionStrings__Flowa", valueFrom = "${local.db_secret_arn}:connection_string::" },
    ]

    healthCheck = {
      command     = ["CMD", "bash", "-c", local.comando_health_check_por_servico.accumulator]
      interval    = 15
      timeout     = 5
      retries     = 3
      startPeriod = 30
    }

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = local.log_group_por_servico.accumulator
        awslogs-region        = var.region
        awslogs-stream-prefix = "app"
      }
    }
  }])
}

# Registro A com TTL de 10 s: depois de um deploy o accumulator troca de IP, e o QuickFIX/n resolve o nome
# de novo a cada tentativa de logon.
resource "aws_service_discovery_service" "registro_dns_do_order_accumulator" {
  name = "${local.prefixo_dos_recursos_flowa}-accumulator"

  dns_config {
    namespace_id   = local.namespace_id
    routing_policy = "MULTIVALUE"

    dns_records {
      type = "A"
      ttl  = 10
    }
  }

  # O ECS tira a instância do Cloud Map quando a task para.
  health_check_custom_config {}

  force_destroy = true
}

# Registro SRV (IP e porta) é o que a integração do API Gateway pelo VPC Link precisa para achar o generator.
resource "aws_service_discovery_service" "registro_srv_do_order_generator" {
  name = "${local.prefixo_dos_recursos_flowa}-generator"

  dns_config {
    namespace_id   = local.namespace_id
    routing_policy = "MULTIVALUE"

    dns_records {
      type = "SRV"
      ttl  = 10
    }
  }

  health_check_custom_config {}

  force_destroy = true
}

# Uma cópia de cada, sem autoscaling, e o deploy derruba a task velha antes de subir a nova: a sessão FIX
# e a exposição não aguentam duas cópias ao mesmo tempo.
resource "aws_ecs_service" "servico_do_order_accumulator" {
  name            = local.nomes_dos_servicos_flowa.accumulator
  cluster         = aws_ecs_cluster.cluster_dos_servicos_flowa.id
  task_definition = aws_ecs_task_definition.tarefa_do_order_accumulator.arn
  launch_type     = "FARGATE"
  desired_count   = 1

  deployment_minimum_healthy_percent = 0
  deployment_maximum_percent         = 100

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = local.subnets_tarefas
    security_groups  = [local.sg_accumulator]
    assign_public_ip = true # sem NAT: é por aqui que a task puxa a imagem e lê o segredo
  }

  service_registries {
    registry_arn = aws_service_discovery_service.registro_dns_do_order_accumulator.arn
  }

  wait_for_steady_state = true

  # A task só sobe depois que a role já pode puxar a imagem, escrever o log e ler o segredo.
  depends_on = [aws_iam_role_policy.permissoes_de_execucao_das_tasks]
}

resource "aws_ecs_service" "servico_do_order_generator" {
  name            = local.nomes_dos_servicos_flowa.generator
  cluster         = aws_ecs_cluster.cluster_dos_servicos_flowa.id
  task_definition = aws_ecs_task_definition.tarefa_do_order_generator.arn
  launch_type     = "FARGATE"
  desired_count   = 1

  deployment_minimum_healthy_percent = 0
  deployment_maximum_percent         = 100

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  network_configuration {
    subnets          = local.subnets_tarefas
    security_groups  = [local.sg_generator]
    assign_public_ip = true
  }

  service_registries {
    registry_arn   = aws_service_discovery_service.registro_srv_do_order_generator.arn
    container_name = "generator"
    container_port = local.porta_http_do_generator
  }

  wait_for_steady_state = true

  # A task só sobe depois que a role já pode puxar a imagem, escrever o log e ler o segredo.
  depends_on = [aws_iam_role_policy.permissoes_de_execucao_das_tasks]
}
