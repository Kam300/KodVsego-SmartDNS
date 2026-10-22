package ru.kodvsego.smartdns;

import org.junit.Test;
import static org.junit.Assert.*;

public class WidgetPinningTest {
    @Test public void unsupportedLauncherDoesNotReceiveRequest(){
        assertEquals(WidgetPinning.Result.MANUAL,WidgetPinning.request(new WidgetPinning.Launcher(){public boolean supported(){return false;}public boolean request(){throw new AssertionError("unsupported request");}}));
    }
    @Test public void rejectionOffersManualInstallation(){assertEquals(WidgetPinning.Result.MANUAL,WidgetPinning.request(launcher(false,false)));}
    @Test public void brokenLauncherOffersManualInstallation(){assertEquals(WidgetPinning.Result.MANUAL,WidgetPinning.request(launcher(false,true)));}
    @Test public void acceptedRequestIsNotReportedAsInstalled(){assertEquals(WidgetPinning.Result.REQUESTED,WidgetPinning.request(launcher(true,false)));}
    private WidgetPinning.Launcher launcher(boolean accepted,boolean broken){return new WidgetPinning.Launcher(){public boolean supported(){return true;}public boolean request(){if(broken)throw new SecurityException("launcher rejected request");return accepted;}};}
}
