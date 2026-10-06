import { DeleteAllConfirmation } from '../components/DeleteAllConfirmation';
import { ExposureByAsset } from '../components/ExposureByAsset';
import { OrderListCard } from '../components/OrderListCard';
import { OrderTicket } from '../components/OrderTicket';
import { OrderListPagination } from '../components/OrderListPagination';
import { TopBar } from '../components/TopBar';
import { useOrdersAndExposures } from '../hooks/useOrdersAndExposures';
import '../styles/order-ticket.css';
import '../styles/order-list.css';
import '../styles/delete-all-confirmation.css';

export function OrderTicketPage() {
  const {
    exposuresState,
    orderListState,
    isSendingOrder,
    lastSendResult,
    orderListPageBeingLoaded,
    loadOrderListPage,
    sendOrderAndRefresh,
    deleteAllOrdersAndRefresh,
  } = useOrdersAndExposures();

  return (
    <div className="page">
      <TopBar />
      <div className="page-header">
        <h1 className="page-title">Boleta de ordens</h1>
        <p className="page-subtitle">
          Envie ordens de compra e venda de PETR4, VALE3 e VIIA4 e acompanhe a exposição de cada ativo até o limite de
          R$ 100.000.000,00.
        </p>
      </div>

      <main className="layout-grid">
        <ExposureByAsset exposuresState={exposuresState} />
        <OrderListCard
          orderListState={orderListState}
          isSendingOrder={isSendingOrder}
          lastSendResult={lastSendResult}
          headerAction={<DeleteAllConfirmation onConfirmDeleteAll={deleteAllOrdersAndRefresh} />}
          footer={
            orderListState.status === 'ready' && (
              <OrderListPagination
                currentPage={orderListState.orderListPage.page}
                totalOrders={orderListState.orderListPage.totalOrders}
                ordersOnPage={orderListState.orderListPage.orders.length}
                pageBeingLoaded={orderListPageBeingLoaded}
                onChoosePage={(chosenPage) => void loadOrderListPage(chosenPage)}
              />
            )
          }
        />
        <OrderTicket isSendingOrder={isSendingOrder} onSendOrder={sendOrderAndRefresh} />
      </main>
    </div>
  );
}
