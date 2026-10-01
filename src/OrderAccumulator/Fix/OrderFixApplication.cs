using OrderAccumulator.Exposure;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;
using Message = QuickFix.Message;

namespace OrderAccumulator.Fix;

// Recebe a NewOrderSingle, entrega ao processador da exposição e responde com o ExecutionReport.
// A validação de campo (D-13), o limite e a ordem repetida (D-11) ficam no IOrderProcessor.
public sealed class OrderFixApplication(IOrderProcessor processor, ILogger<OrderFixApplication> logger)
    : MessageCracker, IApplication
{
    public void FromApp(Message message, SessionID sessionId) => Crack(message, sessionId);

    public void OnMessage(NewOrderSingle order, SessionID sessionId)
    {
        var incoming = new IncomingOrder(
            order.ClOrdID.Value, order.Symbol.Value, order.Side.Value, order.OrderQty.Value, order.Price.Value);

        // O QuickFIX chama cada sessão na sua própria thread e espera o retorno; esperar aqui
        // mantém as respostas na mesma ordem das ordens recebidas.
        var outcome = processor.ProcessAsync(incoming).GetAwaiter().GetResult();
        if (outcome.IsRepeat)
            logger.LogInformation("ClOrdID {ClOrdId} repetido: devolvendo a resposta original.", outcome.ClOrdId);

        Session.SendToTarget(BuildReport(outcome), sessionId);
    }

    // Tags e valores da tabela do ExecutionReport em docs/contracts/contracts.md, seção 2.
    public static ExecutionReport BuildReport(OrderOutcome outcome)
    {
        var report = new ExecutionReport(
            new OrderID(outcome.OrderId),
            new ExecID(outcome.ExecId),
            new ExecType(outcome.Accepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(outcome.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            // O dicionário FIX 4.4 exige a tag 55 na NewOrderSingle, então o símbolo sempre veio.
            new Symbol(outcome.Symbol!),
            new Side(outcome.Side),
            new LeavesQty(outcome.Accepted ? outcome.Quantity : 0m),
            new CumQty(0m),
            new AvgPx(0m))
        {
            ClOrdID = new ClOrdID(outcome.ClOrdId)
        };

        if (!outcome.Accepted)
            report.Text = new Text(outcome.RejectReason!);

        return report;
    }

    public void OnCreate(SessionID sessionId) { }
    public void OnLogon(SessionID sessionId) { }
    public void OnLogout(SessionID sessionId) { }
    public void ToAdmin(Message message, SessionID sessionId) { }
    public void FromAdmin(Message message, SessionID sessionId) { }
    public void ToApp(Message message, SessionID sessionId) { }
}
