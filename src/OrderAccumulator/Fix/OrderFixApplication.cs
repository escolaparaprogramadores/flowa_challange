using OrderAccumulator.Exposure;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;
using Message = QuickFix.Message;

namespace OrderAccumulator.Fix;

// Recebe a NewOrderSingle, entrega ao processador da exposição e responde com o ExecutionReport.
// A validação de campo (D-13), o limite e a ordem repetida (D-11) ficam no IOrderProcessor.
public sealed class OrderFixApplication(IOrderProcessor orderProcessor, ILogger<OrderFixApplication> orderFixLogger)
    : MessageCracker, IApplication
{
    public void FromApp(Message message, SessionID sessionId) => Crack(message, sessionId);

    public void OnMessage(NewOrderSingle newOrderSingle, SessionID sessionId)
    {
        var incomingOrder = new IncomingOrder(
            newOrderSingle.ClOrdID.Value, newOrderSingle.Symbol.Value, newOrderSingle.Side.Value, newOrderSingle.OrderQty.Value, newOrderSingle.Price.Value);

        // O QuickFIX chama cada sessão na sua própria thread e espera o retorno; esperar aqui
        // mantém as respostas na mesma ordem das ordens recebidas.
        OrderOutcome orderOutcome;
        try
        {
            orderOutcome = orderProcessor.ProcessIncomingOrderAsync(incomingOrder).GetAwaiter().GetResult();
        }
        catch (Exception orderProcessingException)
        {
            // Ponto único de erro desta entrada. Sem resposta, o OrderGenerator desiste em 5 s e mostra
            // communication_error (contrato, seção 3). O processador grava numa transação só
            // (PostgresOrderProcessor), então a falha não deixa ordem pela metade; a sessão FIX segue de pé.
            orderFixLogger.LogError(orderProcessingException, "Falha ao processar a ordem {ClOrdId}; nenhum ExecutionReport enviado.", incomingOrder.ClOrdId);
            return;
        }

        if (orderOutcome.IsRepeat)
            orderFixLogger.LogInformation("ClOrdID {ClOrdId} repetido: devolvendo a resposta original.", orderOutcome.ClOrdId);

        // A ordem já está gravada. Se a sessão caiu antes da resposta, reenviar o mesmo ClOrdID devolve
        // a resposta gravada (D-11); o aviso deixa o caso visível no log.
        if (!Session.SendToTarget(BuildExecutionReport(orderOutcome), sessionId))
            orderFixLogger.LogWarning("ExecutionReport da ordem {ClOrdId} não foi enviado: a sessão FIX não está logada.", orderOutcome.ClOrdId);
    }

    // Tags e valores da tabela do ExecutionReport em docs/contracts/contracts.md, seção 2.
    public static ExecutionReport BuildExecutionReport(OrderOutcome orderOutcome)
    {
        var executionReport = new ExecutionReport(
            new OrderID(orderOutcome.OrderId),
            new ExecID(orderOutcome.ExecId),
            new ExecType(orderOutcome.Accepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(orderOutcome.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            // O dicionário FIX 4.4 exige a tag 55 na NewOrderSingle, então o símbolo sempre veio.
            new Symbol(orderOutcome.Symbol!),
            new Side(orderOutcome.Side),
            new LeavesQty(orderOutcome.Accepted ? orderOutcome.Quantity : 0m),
            new CumQty(0m),
            new AvgPx(0m))
        {
            ClOrdID = new ClOrdID(orderOutcome.ClOrdId)
        };

        if (!orderOutcome.Accepted)
            executionReport.Text = new Text(orderOutcome.RejectReason!);

        return executionReport;
    }

    public void OnCreate(SessionID sessionId) { }
    public void OnLogon(SessionID sessionId) { }
    public void OnLogout(SessionID sessionId) { }
    public void ToAdmin(Message message, SessionID sessionId) { }
    public void FromAdmin(Message message, SessionID sessionId) { }
    public void ToApp(Message message, SessionID sessionId) { }
}
