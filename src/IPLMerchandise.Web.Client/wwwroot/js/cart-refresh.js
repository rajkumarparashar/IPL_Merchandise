// Tells the cart and checkout pages when the customer returns to the tab, so they can catch up on any
// product-change pushes missed while the tab was in the background (browsers may suspend connections).
export function watch(dotnet) {
  const onReturn = () => {
    if (document.visibilityState === 'visible') dotnet.invokeMethodAsync('OnVisibleAsync');
  };
  document.addEventListener('visibilitychange', onReturn);
  return {
    dispose() {
      document.removeEventListener('visibilitychange', onReturn);
    }
  };
}
