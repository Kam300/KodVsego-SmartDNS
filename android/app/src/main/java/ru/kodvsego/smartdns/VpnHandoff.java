package ru.kodvsego.smartdns;

import java.io.IOException;
import java.io.InterruptedIOException;
import java.util.function.BooleanSupplier;

/** Android may publish the old VPN network briefly after permission was transferred. */
final class VpnHandoff {
    interface Pause {void next() throws InterruptedException;}
    static void await(BooleanSupplier released,BooleanSupplier valid,Pause pause) throws IOException {
        int stable=0;
        for(int attempt=0;attempt<40;attempt++){
            check(valid);
            stable=released.getAsBoolean()?stable+1:0;
            if(stable>=2){check(valid);return;}
            if(attempt<39)try{pause.next();}catch(InterruptedException e){Thread.currentThread().interrupt();throw new InterruptedIOException("Переключение VPN отменено");}
        }
        check(valid);
        throw new IOException("Предыдущий VPN не завершил отключение или подключился снова. Отключите его; проверьте «Постоянный VPN» и блокировку подключений без VPN в настройках Android.");
    }
    private static void check(BooleanSupplier valid) throws InterruptedIOException {if(!valid.getAsBoolean()||Thread.currentThread().isInterrupted())throw new InterruptedIOException("Переключение VPN отменено");}
}
