package ru.kodvsego.smartdns;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.*;

/** Grants the system installer read-only access to exactly one verified cache file. */
public final class UpdateFileProvider extends ContentProvider {
    static Uri uri(Context context){return Uri.parse("content://"+context.getPackageName()+".updates/apk");}
    private void require(Uri uri){if(getContext()==null||!uri.equals(uri(getContext())))throw new SecurityException("Неизвестный APK");}
    private File file(){return new File(getContext().getCacheDir(),"updates/verified.apk");}
    @Override public boolean onCreate(){return true;}
    @Override public String getType(Uri uri){require(uri);return "application/vnd.android.package-archive";}
    @Override public ParcelFileDescriptor openFile(Uri uri,String mode)throws FileNotFoundException{require(uri);if(!"r".equals(mode))throw new FileNotFoundException("Только чтение");return ParcelFileDescriptor.open(file(),ParcelFileDescriptor.MODE_READ_ONLY);}
    @Override public Cursor query(Uri uri,String[] projection,String selection,String[] args,String sort){
        require(uri);String[] columns=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;
        MatrixCursor cursor=new MatrixCursor(columns);Object[] values=new Object[columns.length];for(int i=0;i<columns.length;i++)values[i]=OpenableColumns.DISPLAY_NAME.equals(columns[i])?"SmartDNS-update.apk":OpenableColumns.SIZE.equals(columns[i])?file().length():null;cursor.addRow(values);return cursor;
    }
    @Override public Uri insert(Uri uri,ContentValues values){throw new UnsupportedOperationException();}
    @Override public int delete(Uri uri,String selection,String[] args){throw new UnsupportedOperationException();}
    @Override public int update(Uri uri,ContentValues values,String selection,String[] args){throw new UnsupportedOperationException();}
}
