locals {
  # flowa.exposicao e flowa.ordens.* vêm só do worker datadog-metrics. Os gráficos de trace e de runtime
  # usam a variável $service (padrão order-accumulator): quem gera esses dados é o Accumulator.
  filtro_do_worker_de_metricas = "service:datadog-metrics"
}

# Este arquivo é o painel inteiro: gráfico criado à mão no Datadog some no próximo apply.
resource "datadog_dashboard" "ordens_e_exposicao" {
  description = "Ordens aceitas e rejeitadas pelo OrderAccumulator e a exposição de cada símbolo, com o limite de ±100.000.000."
  layout_type = "ordered"
  reflow_type = "fixed"
  title       = "Flowa - ordens e exposição (dev)"
  # `default` (e não `defaults`) é o que o painel ao vivo guarda; com `defaults` o plan mostraria diferença.
  template_variable {
    default = "dev"
    name    = "env"
    prefix  = "env"
  }
  template_variable {
    default = "order-accumulator"
    name    = "service"
    prefix  = "service"
  }
  template_variable {
    default = "*"
    name    = "symbol"
    prefix  = "symbol"
  }
  template_variable {
    available_values = ["buy", "sell"]
    default          = "*"
    name             = "side"
    prefix           = "side"
  }
  template_variable_preset {
    name = "Somente compras"
    template_variable {
      name   = "side"
      values = ["buy"]
    }
  }
  template_variable_preset {
    name = "Somente vendas"
    template_variable {
      name   = "side"
      values = ["sell"]
    }
  }
  widget {
    note_definition {
      background_color = "white"
      content          = "# Flowa · Ordens e Exposição\n**OrderAccumulator** · ordens aceitas e rejeitadas e exposição líquida por símbolo · limite de **±100.000.000** por símbolo (ambiente `dev`)\n\nAtalhos: [Four Golden Signals](/dashboard/yxn-fj9-c9a) · [Traces do accumulator](/apm/traces?query=env%3Adev%20service%3Aorder-accumulator) · [Rejeições (spans)](/apm/traces?query=env%3Adev%20service%3Aorder-accumulator%20resource_name%3Afix.recebimento_da_ordem) · [Métrica de exposição](/metric/explorer?exp_metric=flowa.exposicao&exp_group=symbol)"
      font_size        = "14"
      has_padding      = true
      text_align       = "left"
      vertical_align   = "top"
    }
    widget_layout {
      height = 4
      width  = 9
      x      = 0
      y      = 0
    }
  }
  widget {
    note_definition {
      background_color = "gray"
      content          = "**Uso do limite**\n\n- 🟢 abaixo de 80%\n- 🟡 de 80% a 90%\n- 🔴 a partir de 90%\n\n**Taxa de aceite**\n\n- 🟢 ≥ 95% · 🟡 ≥ 80% · 🔴 < 80%\n\nExposição = soma assinada (compra +, venda −)."
      font_size        = "12"
      has_padding      = true
      text_align       = "left"
      vertical_align   = "top"
    }
    widget_layout {
      height = 4
      width  = 3
      x      = 9
      y      = 0
    }
  }
  widget {
    group_definition {
      background_color = "vivid_blue"
      layout_type      = "ordered"
      show_title       = true
      title            = "Visão geral"
      widget {
        query_value_definition {
          autoscale   = true
          precision   = 0
          title       = "Total de ordens"
          title_align = "center"
          title_size  = "16"
          request {
            formula {
              formula_expression = "default_zero(a) + default_zero(b)"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "b"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
          }
          timeseries_background {
            type = "bars"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 0
          y      = 0
        }
      }
      widget {
        query_value_definition {
          autoscale   = true
          precision   = 0
          title       = "Aceitas"
          title_align = "center"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">="
              palette    = "green_on_white"
              value      = 0
            }
            formula {
              formula_expression = "default_zero(a)"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
          }
          timeseries_background {
            type = "bars"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 2
          y      = 0
        }
      }
      widget {
        query_value_definition {
          autoscale   = true
          precision   = 0
          title       = "Rejeitadas"
          title_align = "center"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">"
              palette    = "red_on_white"
              value      = 0
            }
            conditional_formats {
              comparator = "<="
              palette    = "green_on_white"
              value      = 0
            }
            formula {
              formula_expression = "default_zero(a)"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
          }
          timeseries_background {
            type = "bars"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 4
          y      = 0
        }
      }
      widget {
        query_value_definition {
          autoscale   = true
          custom_unit = "%"
          precision   = 2
          title       = "Taxa de aceite"
          title_align = "center"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">="
              palette    = "green_on_white"
              value      = 95
            }
            conditional_formats {
              comparator = ">="
              palette    = "yellow_on_white"
              value      = 80
            }
            conditional_formats {
              comparator = "<"
              palette    = "red_on_white"
              value      = 80
            }
            formula {
              formula_expression = "100 * default_zero(a) / (default_zero(a) + default_zero(b))"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "b"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 6
          y      = 0
        }
      }
      widget {
        query_value_definition {
          autoscale   = true
          custom_unit = "/min"
          precision   = 0
          title       = "Ritmo de ordens"
          title_align = "center"
          title_size  = "16"
          request {
            formula {
              formula_expression = "(default_zero(a) + default_zero(b)) * 60"
            }
            query {
              metric_query {
                aggregator = "avg"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_rate()"
              }
            }
            query {
              metric_query {
                aggregator = "avg"
                name       = "b"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_rate()"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 8
          y      = 0
        }
      }
      widget {
        query_value_definition {
          autoscale   = true
          custom_unit = "ms"
          precision   = 1
          title       = "Latência de registro p95"
          title_align = "center"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">"
              palette    = "red_on_white"
              value      = 200
            }
            conditional_formats {
              comparator = ">"
              palette    = "yellow_on_white"
              value      = 50
            }
            conditional_formats {
              comparator = "<="
              palette    = "green_on_white"
              value      = 50
            }
            formula {
              formula_expression = "a * 1000"
            }
            query {
              metric_query {
                aggregator = "avg"
                name       = "a"
                query      = "p95:trace.consumer{$env,$service}"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 2
          x      = 10
          y      = 0
        }
      }
      widget {
        query_value_definition {
          precision   = 0
          title       = "Maior exposição comprada"
          title_align = "center"
          title_size  = "16"
          request {
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol}"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 3
          x      = 0
          y      = 2
        }
      }
      widget {
        query_value_definition {
          precision   = 0
          title       = "Maior exposição vendida"
          title_align = "center"
          title_size  = "16"
          request {
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "min:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol}"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 3
          x      = 3
          y      = 2
        }
      }
      widget {
        query_value_definition {
          precision   = 0
          title       = "Exposição líquida total"
          title_align = "center"
          title_size  = "16"
          request {
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "sum:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol}"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 3
          x      = 6
          y      = 2
        }
      }
      widget {
        query_value_definition {
          custom_unit = "%"
          precision   = 3
          title       = "Pico de uso do limite"
          title_align = "center"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">"
              palette    = "red_on_white"
              value      = 90
            }
            conditional_formats {
              comparator = ">"
              palette    = "yellow_on_white"
              value      = 80
            }
            conditional_formats {
              comparator = "<="
              palette    = "green_on_white"
              value      = 80
            }
            formula {
              formula_expression = "100 * (abs(a) + abs(b) + abs(abs(a) - abs(b))) / 2 / 100000000"
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol}"
              }
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "b"
                query      = "min:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol}"
              }
            }
          }
          timeseries_background {
            type = "area"
            yaxis {
            }
          }
        }
        widget_layout {
          height = 2
          width  = 3
          x      = 9
          y      = 2
        }
      }
      widget {
        query_table_definition {
          has_search_bar = "never"
          title          = "Painel de risco por símbolo"
          title_align    = "left"
          title_size     = "16"
          request {
            limit = 0
            formula {
              alias              = "exposição"
              cell_display_mode  = "number"
              formula_expression = "e"
            }
            formula {
              alias              = "uso do limite %"
              cell_display_mode  = "bar"
              formula_expression = "100 * abs(e) / 100000000"
              conditional_formats {
                comparator = ">"
                palette    = "red_on_white"
                value      = 90
              }
              conditional_formats {
                comparator = ">"
                palette    = "yellow_on_white"
                value      = 80
              }
              conditional_formats {
                comparator = "<="
                palette    = "green_on_white"
                value      = 80
              }
              limit {
                count = 50
                order = "desc"
              }
            }
            formula {
              alias              = "folga até o limite"
              formula_expression = "100000000 - abs(e)"
            }
            formula {
              alias              = "aceitas"
              formula_expression = "default_zero(a)"
            }
            formula {
              alias              = "rejeitadas"
              formula_expression = "default_zero(r)"
              conditional_formats {
                comparator = ">"
                palette    = "red_on_white"
                value      = 0
              }
              conditional_formats {
                comparator = "<="
                palette    = "green_on_white"
                value      = 0
              }
            }
            formula {
              alias              = "rejeição %"
              formula_expression = "100 * default_zero(r) / (default_zero(a) + default_zero(r))"
              conditional_formats {
                comparator = ">"
                palette    = "red_on_white"
                value      = 20
              }
              conditional_formats {
                comparator = ">"
                palette    = "yellow_on_white"
                value      = 5
              }
              conditional_formats {
                comparator = "<="
                palette    = "green_on_white"
                value      = 5
              }
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "e"
                query      = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}"
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}.as_count()"
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "r"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}.as_count()"
              }
            }
          }
        }
        widget_layout {
          height = 4
          width  = 7
          x      = 0
          y      = 4
        }
      }
      widget {
        sunburst_definition {
          title       = "Mix de ordens · símbolo › lado"
          title_align = "left"
          title_size  = "16"
          request {
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol,side}.as_count()"
              }
            }
          }
        }
        widget_layout {
          height = 4
          width  = 5
          x      = 7
          y      = 4
        }
      }
    }
    widget_layout {
      height = 9
      width  = 12
      x      = 0
      y      = 4
    }
  }
  widget {
    group_definition {
      background_color = "vivid_orange"
      layout_type      = "ordered"
      show_title       = true
      title            = "Exposição e limites"
      widget {
        note_definition {
          background_color = "orange"
          content          = "**Exposição e limites** — exposição líquida assinada por símbolo (compras somam, vendas subtraem). Uma ordem é **rejeitada** quando levaria |exposição| acima de **100.000.000**. Acompanhe o **% do limite** e a **folga**: quanto menor a folga, maior a chance de rejeição."
          font_size        = "14"
          has_padding      = true
          text_align       = "left"
          vertical_align   = "center"
        }
        widget_layout {
          height = 1
          width  = 12
          x      = 0
          y      = 0
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Exposição por símbolo"
          title_align    = "left"
          title_size     = "16"
          marker {
            display_type = "error bold"
            label        = "limite +100M"
            value        = "y = 100000000"
          }
          marker {
            display_type = "error bold"
            label        = "limite −100M"
            value        = "y = -100000000"
          }
          marker {
            display_type = "warning dashed"
            label        = "90%"
            value        = "y = 90000000"
          }
          marker {
            display_type = "warning dashed"
            label        = "−90%"
            value        = "y = -90000000"
          }
          request {
            display_type = "line"
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                name  = "a"
                query = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}"
              }
            }
            style {
              line_type  = "solid"
              line_width = "thick"
              palette    = "dog_classic"
            }
          }
          yaxis {
            include_zero = true
          }
        }
        widget_layout {
          height = 3
          width  = 8
          x      = 0
          y      = 1
        }
      }
      widget {
        toplist_definition {
          title       = "Exposição agora"
          title_align = "left"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = ">="
              palette    = "white_on_red"
              value      = 90000000
            }
            conditional_formats {
              comparator = ">="
              palette    = "white_on_yellow"
              value      = 80000000
            }
            conditional_formats {
              comparator = "<="
              palette    = "white_on_red"
              value      = -90000000
            }
            conditional_formats {
              comparator = "<="
              palette    = "white_on_yellow"
              value      = -80000000
            }
            conditional_formats {
              comparator = ">"
              palette    = "white_on_green"
              value      = -80000000
            }
            formula {
              formula_expression = "a"
              limit {
                count = 20
                order = "desc"
              }
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}"
              }
            }
          }
          style {
            palette = "dog_classic"
            display {
              type = "flat"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 8
          y      = 1
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Uso do limite por símbolo (%)"
          title_align    = "left"
          title_size     = "16"
          marker {
            display_type = "error bold"
            label        = "limite"
            value        = "y = 100"
          }
          marker {
            display_type = "error dashed"
            label        = "90%"
            value        = "y = 90"
          }
          marker {
            display_type = "warning dashed"
            label        = "80%"
            value        = "y = 80"
          }
          request {
            display_type = "line"
            formula {
              formula_expression = "100 * abs(a) / 100000000"
            }
            query {
              metric_query {
                name  = "a"
                query = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "warm"
            }
          }
          yaxis {
            max = "105"
            min = "0"
          }
        }
        widget_layout {
          height = 3
          width  = 6
          x      = 0
          y      = 4
        }
      }
      widget {
        toplist_definition {
          title       = "Folga até o limite (menor = mais risco)"
          title_align = "left"
          title_size  = "16"
          request {
            conditional_formats {
              comparator = "<="
              palette    = "white_on_red"
              value      = 10000000
            }
            conditional_formats {
              comparator = "<="
              palette    = "white_on_yellow"
              value      = 20000000
            }
            conditional_formats {
              comparator = ">"
              palette    = "white_on_green"
              value      = 20000000
            }
            formula {
              formula_expression = "100000000 - abs(a)"
              limit {
                count = 20
                order = "asc"
              }
            }
            query {
              metric_query {
                aggregator = "last"
                name       = "a"
                query      = "max:flowa.exposicao{$env,${local.filtro_do_worker_de_metricas},$symbol} by {symbol}"
              }
            }
          }
          style {
            palette = "dog_classic"
            display {
              type = "flat"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 6
          x      = 6
          y      = 4
        }
      }
    }
    widget_layout {
      height = 8
      width  = 12
      x      = 0
      y      = 13
    }
  }
  widget {
    group_definition {
      background_color = "vivid_green"
      layout_type      = "ordered"
      show_title       = true
      title            = "Fluxo de ordens"
      widget {
        note_definition {
          background_color = "green"
          content          = "**Fluxo de ordens** — volume recebido e resultado da validação. Rejeições concentradas = símbolo encostado no limite; queda de volume = gerador parado ou falha no FIX."
          font_size        = "14"
          has_padding      = true
          text_align       = "left"
          vertical_align   = "center"
        }
        widget_layout {
          height = 1
          width  = 12
          x      = 0
          y      = 0
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Ordens aceitas x rejeitadas"
          title_align    = "left"
          title_size     = "16"
          request {
            display_type = "bars"
            formula {
              alias              = "aceitas"
              formula_expression = "default_zero(a)"
              style {
                palette       = "green"
                palette_index = 3
              }
            }
            formula {
              alias              = "rejeitadas"
              formula_expression = "default_zero(b)"
              style {
                palette       = "warm"
                palette_index = 4
              }
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            query {
              metric_query {
                name  = "b"
                query = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "dog_classic"
            }
          }
          request {
            display_type = "line"
            formula {
              alias              = "total · 1h antes"
              formula_expression = "hour_before(default_zero(c) + default_zero(d))"
              style {
                palette       = "gray"
                palette_index = 2
              }
            }
            query {
              metric_query {
                name  = "c"
                query = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            query {
              metric_query {
                name  = "d"
                query = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            style {
              line_type  = "dashed"
              line_width = "thin"
              palette    = "gray"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 8
          x      = 0
          y      = 1
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "horizontal"
          show_legend    = true
          title          = "Taxa de aceite (%)"
          title_align    = "left"
          title_size     = "16"
          marker {
            display_type = "ok dashed"
            label        = "95%"
            value        = "y = 95"
          }
          marker {
            display_type = "error dashed"
            label        = "80%"
            value        = "y = 80"
          }
          request {
            display_type = "line"
            formula {
              alias              = "taxa de aceite"
              formula_expression = "100 * default_zero(a) / (default_zero(a) + default_zero(b))"
              style {
                palette       = "green"
                palette_index = 3
              }
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            query {
              metric_query {
                name  = "b"
                query = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "thick"
              palette    = "green"
            }
          }
          yaxis {
            max = "101"
            min = "0"
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 8
          y      = 1
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Ordens por símbolo"
          title_align    = "left"
          title_size     = "16"
          request {
            display_type = "bars"
            formula {
              formula_expression = "default_zero(a) + default_zero(b)"
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol}.as_count()"
              }
            }
            query {
              metric_query {
                name  = "b"
                query = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "cool"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 0
          y      = 4
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Ordens por lado (compra x venda)"
          title_align    = "left"
          title_size     = "16"
          request {
            display_type = "bars"
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {side}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "purple"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 4
          y      = 4
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "auto"
          show_legend    = true
          title          = "Rejeições por símbolo e lado"
          title_align    = "left"
          title_size     = "16"
          request {
            display_type = "bars"
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol,side}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "warm"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 8
          y      = 4
        }
      }
      widget {
        change_definition {
          title       = "Volume por símbolo vs. 1 hora antes"
          title_align = "left"
          title_size  = "16"
          request {
            change_type   = "relative"
            compare_to    = "hour_before"
            increase_good = true
            order_by      = "change"
            order_dir     = "desc"
            show_present  = true
            formula {
              formula_expression = "hour_before(a)"
            }
            formula {
              formula_expression = "a"
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol}.as_count()"
              }
            }
          }
        }
        widget_layout {
          height = 3
          width  = 6
          x      = 0
          y      = 7
        }
      }
      widget {
        query_table_definition {
          has_search_bar = "never"
          title          = "Resumo por símbolo e lado"
          title_align    = "left"
          title_size     = "16"
          request {
            limit = 0
            formula {
              alias              = "aceitas"
              cell_display_mode  = "bar"
              formula_expression = "default_zero(a)"
              limit {
                count = 50
                order = "desc"
              }
            }
            formula {
              alias              = "rejeitadas"
              formula_expression = "default_zero(r)"
              conditional_formats {
                comparator = ">"
                palette    = "red_on_white"
                value      = 0
              }
              conditional_formats {
                comparator = "<="
                palette    = "green_on_white"
                value      = 0
              }
            }
            formula {
              alias              = "aceite %"
              formula_expression = "100 * default_zero(a) / (default_zero(a) + default_zero(r))"
              conditional_formats {
                comparator = ">="
                palette    = "green_on_white"
                value      = 95
              }
              conditional_formats {
                comparator = ">="
                palette    = "yellow_on_white"
                value      = 80
              }
              conditional_formats {
                comparator = "<"
                palette    = "red_on_white"
                value      = 80
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "a"
                query      = "sum:flowa.ordens.aceitas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol,side}.as_count()"
              }
            }
            query {
              metric_query {
                aggregator = "sum"
                name       = "r"
                query      = "sum:flowa.ordens.rejeitadas{$env,${local.filtro_do_worker_de_metricas},$symbol,$side} by {symbol,side}.as_count()"
              }
            }
          }
        }
        widget_layout {
          height = 3
          width  = 6
          x      = 6
          y      = 7
        }
      }
    }
    widget_layout {
      height = 11
      width  = 12
      x      = 0
      y      = 21
    }
  }
  widget {
    group_definition {
      background_color = "vivid_purple"
      layout_type      = "ordered"
      show_title       = true
      title            = "Saúde do processamento"
      widget {
        note_definition {
          background_color = "purple"
          content          = "**Saúde do processamento** — latência e erros do accumulator ao registrar ordens. Detalhes em [Four Golden Signals](/dashboard/yxn-fj9-c9a)."
          font_size        = "14"
          has_padding      = true
          text_align       = "left"
          vertical_align   = "center"
        }
        widget_layout {
          height = 1
          width  = 12
          x      = 0
          y      = 0
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "horizontal"
          show_legend    = true
          title          = "Recebimento FIX · p50 / p95 / p99 (ms)"
          title_align    = "left"
          title_size     = "16"
          marker {
            display_type = "error dashed"
            label        = "200 ms"
            value        = "y = 200"
          }
          marker {
            display_type = "warning dashed"
            label        = "50 ms"
            value        = "y = 50"
          }
          request {
            display_type = "line"
            formula {
              alias              = "p50"
              formula_expression = "a * 1000"
              style {
                palette       = "purple"
                palette_index = 1
              }
            }
            formula {
              alias              = "p95"
              formula_expression = "b * 1000"
              style {
                palette       = "purple"
                palette_index = 3
              }
            }
            formula {
              alias              = "p99"
              formula_expression = "c * 1000"
              style {
                palette       = "purple"
                palette_index = 5
              }
            }
            query {
              metric_query {
                name  = "a"
                query = "p50:trace.consumer{$env,$service}"
              }
            }
            query {
              metric_query {
                name  = "b"
                query = "p95:trace.consumer{$env,$service}"
              }
            }
            query {
              metric_query {
                name  = "c"
                query = "p99:trace.consumer{$env,$service}"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "dog_classic"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 0
          y      = 1
        }
      }
      widget {
        toplist_definition {
          title       = "Postgres · p95 por query (ms)"
          title_align = "left"
          title_size  = "16"
          request {
            formula {
              formula_expression = "a * 1000"
              limit {
                count = 6
                order = "desc"
              }
            }
            query {
              metric_query {
                aggregator = "avg"
                name       = "a"
                query      = "p95:trace.postgres.query{$env,$service} by {resource_name}"
              }
            }
          }
          style {
            palette = "purple"
            display {
              type = "flat"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 4
          y      = 1
        }
      }
      widget {
        timeseries_definition {
          legend_columns = ["avg", "max", "value"]
          legend_layout  = "horizontal"
          show_legend    = true
          title          = "Erros no processamento"
          title_align    = "left"
          title_size     = "16"
          request {
            display_type = "bars"
            formula {
              alias              = "FIX recebimento"
              formula_expression = "default_zero(a)"
              style {
                palette       = "warm"
                palette_index = 2
              }
            }
            formula {
              alias              = "Postgres"
              formula_expression = "default_zero(b)"
              style {
                palette       = "warm"
                palette_index = 4
              }
            }
            query {
              metric_query {
                name  = "a"
                query = "sum:trace.consumer.errors{$env,$service}.as_count()"
              }
            }
            query {
              metric_query {
                name  = "b"
                query = "sum:trace.postgres.query.errors{$env,$service}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "normal"
              palette    = "warm"
            }
          }
          request {
            display_type = "line"
            formula {
              alias              = "exceções .NET"
              formula_expression = "default_zero(c)"
              style {
                palette       = "red"
                palette_index = 3
              }
            }
            query {
              metric_query {
                name  = "c"
                query = "sum:runtime.dotnet.exceptions.count{$env,$service}.as_count()"
              }
            }
            style {
              line_type  = "solid"
              line_width = "thick"
              palette    = "red"
            }
          }
        }
        widget_layout {
          height = 3
          width  = 4
          x      = 8
          y      = 1
        }
      }
    }
    widget_layout {
      height = 5
      width  = 12
      x      = 0
      y      = 32
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
