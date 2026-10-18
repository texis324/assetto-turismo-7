using AcManager.Pages.Dialogs;
using AcManager.Tools.SemiGui;

namespace AcManager.Tools {
    public class GameWrapperUiFactory : IAnyFactory<IGameUi>, IAnyFactory<IBookingUi> {
        IGameUi IAnyFactory<IGameUi>.Create() => (IGameUi)Pages.Gt.GtGameUi.Pending ?? new GameDialog();   // [fork] GT7 コースイン中は専用 UI

        IBookingUi IAnyFactory<IBookingUi>.Create() => new BookingDialog();
    }
}