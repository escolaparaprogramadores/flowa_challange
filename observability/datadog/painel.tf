locals {
  # As métricas que o OrderAccumulator envia (src/OrderAccumulator/Observabilidade/OrderMetrics.cs).
  filtro_do_order_accumulator = "env:dev,service:order-accumulator"
  ordens_aceitas              = "sum:flowa.ordens.aceitas{${local.filtro_do_order_accumulator}}.as_count()"
  ordens_rejeitadas           = "sum:flowa.ordens.rejeitadas{${local.filtro_do_order_accumulator}}.as_count()"
  exposicao_por_simbolo       = "max:flowa.exposicao{${local.filtro_do_order_accumulator}} by {symbol}"
}

resource "datadog_dashboard" "ordens_e_exposicao" {
  title       = "Flowa - ordens e exposição (dev)"
  description = "Ordens aceitas e rejeitadas pelo OrderAccumulator e a exposição de cada símbolo, com o limite de 100.000.000."
  layout_type = "ordered"

  widget {
    query_value_definition {
      title       = "Taxa de aceite no período"
      precision   = 1
      custom_unit = "%"

      request {
        formula {
          formula_expression = "100 * aceitas / (aceitas + rejeitadas)"
        }
        query {
          metric_query {
            name        = "aceitas"
            data_source = "metrics"
            query       = local.ordens_aceitas
            aggregator  = "sum"
          }
        }
        query {
          metric_query {
            name        = "rejeitadas"
            data_source = "metrics"
            query       = local.ordens_rejeitadas
            aggregator  = "sum"
          }
        }
      }
    }
  }

  widget {
    timeseries_definition {
      title       = "Ordens aceitas e rejeitadas"
      show_legend = true

      request {
        display_type = "bars"
        formula {
          formula_expression = "aceitas"
          alias              = "aceitas"
        }
        formula {
          formula_expression = "rejeitadas"
          alias              = "rejeitadas"
        }
        query {
          metric_query {
            name        = "aceitas"
            data_source = "metrics"
            query       = local.ordens_aceitas
          }
        }
        query {
          metric_query {
            name        = "rejeitadas"
            data_source = "metrics"
            query       = local.ordens_rejeitadas
          }
        }
      }
    }
  }

  widget {
    timeseries_definition {
      title       = "Exposição por símbolo"
      show_legend = true

      request {
        display_type = "line"
        q            = local.exposicao_por_simbolo
      }

      marker {
        value        = "y = 100000000"
        display_type = "error dashed"
        label        = "limite de compra"
      }
      marker {
        value        = "y = -100000000"
        display_type = "error dashed"
        label        = "limite de venda"
      }
    }
  }

  widget {
    toplist_definition {
      title = "Exposição agora"

      request {
        q = local.exposicao_por_simbolo
      }
    }
  }

  # O link público é ligado à mão pelo dono ("Share → Public") e morre se o painel for recriado.
  lifecycle {
    prevent_destroy = true
  }
}

output "endereco_do_painel" {
  description = "Caminho do painel dentro da conta do Datadog."
  value       = datadog_dashboard.ordens_e_exposicao.url
  sensitive   = true
}
