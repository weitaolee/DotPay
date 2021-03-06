﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DFramework;
using MySql.Data.MySqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Dotpay.TaobaoMonitor
{
    internal class LoseTransactionRevalidator
    {
        private const string RIPPLE_VALIDATE_EXCHANGE_NAME = "__RippleValidate_Exchange";
        private const string RIPPLE_VALIDATE_QUEUE = "__RippleValidate_LedgerIndex_Tx";
        private const string RIPPLE_VALIDATE_QUEUE_REPLY = "__RippleValidate_LedgerIndex_Reply";
        private static bool _started;

        private static readonly string MysqlConnectionString =
            ConfigurationManagerWrapper.GetDBConnectionString("taobaodb");

        private static readonly string RabbitMqConnectionString =
            ConfigurationManagerWrapper.GetDBConnectionString("messageQueueServerConnectString");

        private static LedgerIndexRpcClient _ledgerIndexRpcClient;
        private static ValidatorRpcClient _validatorRpcClient;

        public static void Start()
        {
            if (_started) return;

            var thread = new Thread(() =>
            {
                var factory = new ConnectionFactory { Uri = RabbitMqConnectionString, AutomaticRecoveryEnabled = true };
                var connection = factory.CreateConnection();
                _ledgerIndexRpcClient = new LedgerIndexRpcClient(connection);
                _validatorRpcClient = new ValidatorRpcClient(connection);

                while (true)
                {
                    try
                    {
                        var loseTxs = GetLoseTransaction();
                        var completeLedgerIndex = 0L;

                        if (loseTxs.Any())
                        {
                            completeLedgerIndex = _ledgerIndexRpcClient.GetLastLedgerIndex().Result;

                            if (completeLedgerIndex != -1)
                            {
                                loseTxs.ForEach(lt =>
                                {
                                    Log.Info("发现丢失结果的的ripple tx : txid=" + lt.txid + ",tid=" + lt.tid + ",amount=" +
                                             lt.amount + ",address=" + lt.ripple_address);
                                    if (lt.tx_lastLedgerSequence <= completeLedgerIndex)
                                    {
                                        Log.Info("发现丢失结果的tx的最大ledger已经过了-->ripple tx : txid=" + lt.txid + ",tid=" +
                                                 lt.tid + ",amount=" + lt.amount + ",address=" + lt.ripple_address);
                                        var result = _validatorRpcClient.ValidateTx(lt.txid).Result;

                                        if (result == 1)
                                        {
                                            Log.Info("发现丢失结果的tx已经成功了-->ripple tx : txid=" + lt.txid + ",tid=" + lt.tid +
                                                     ",amount=" + lt.amount + ",address=" + lt.ripple_address);
                                            //tx已成功
                                            var success = MarkTxSuccess(lt.tid) == 1;
                                            Log.Info("标记db结果为success,结果=" + success + "-->ripple tx : txid=" + lt.txid +
                                                     ",tid=" + lt.tid + ",amount=" + lt.amount + ",address=" +
                                                     lt.ripple_address);
                                        }
                                        else if (result == 0)
                                        {
                                            Log.Info("发现丢失结果的tx not found-->ripple tx : txid=" + lt.txid + ",tid=" +
                                                     lt.tid + ",amount=" + lt.amount + ",address=" + lt.ripple_address);

                                            //tx已失败
                                            var success = MarkTxAsInitForNextProccesLoop(lt.tid);

                                            Log.Info("初始化掉DB记录，让dispatcher自动重新提交,结果=" + success + "-->ripple tx : txid=" +
                                                     lt.txid + ",tid=" + lt.tid + ",amount=" + lt.amount + ",address=" +
                                                     lt.ripple_address);
                                        }
                                        else
                                        {
                                            //未决的tx,应等待最后结果
                                            Log.Info("发现丢失结果的tx最终结果还未确定-->ripple tx : txid=" + lt.txid + ",tid=" +
                                                     lt.tid + ",amount=" + lt.amount + ",address=" + lt.ripple_address);
                                        }
                                    }
                                });
                            }
                            else
                            {
                                Log.Debug("completeLedgerIndex 结果=" + completeLedgerIndex);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("  Exception", ex);
                    }

                    Task.Delay(6 * 1000).Wait();
                }
            });

            thread.Start();
            Log.Info("-->ripple丢失tx验证器启动成功...");
            _started = true;
        }

        #region rpc client

        private class LedgerIndexRpcClient
        {
            private readonly IModel channel;
            private readonly DefaultBasicConsumer consumer;
            private readonly string replyQueueName;
            private IConnection connection;
            private ConcurrentDictionary<string, TaskCompletionSource<long>> requsetDic = new ConcurrentDictionary<string, TaskCompletionSource<long>>();

            public LedgerIndexRpcClient(IConnection connection)
            {
                this.connection = connection;
                channel = connection.CreateModel();
                replyQueueName = channel.QueueDeclare(RIPPLE_VALIDATE_QUEUE_REPLY, true, false, false, null);
                channel.ExchangeDeclare(RIPPLE_VALIDATE_EXCHANGE_NAME, ExchangeType.Direct);
                channel.QueueDeclare(RIPPLE_VALIDATE_QUEUE, true, false, false, null);
                channel.QueueBind(RIPPLE_VALIDATE_QUEUE, RIPPLE_VALIDATE_EXCHANGE_NAME, "");
                consumer = new ValidatorMessageConsumer(channel, (p, b) =>
                {
                    TaskCompletionSource<long> ts;
                    requsetDic.TryRemove(p.CorrelationId, out ts);

                    if (ts != null)
                    {
                        try
                        {
                            var result = Convert.ToInt64(Encoding.UTF8.GetString(b));
                            ts.SetResult(result);
                        }
                        catch
                        {
                            ts.SetResult(-1);
                        }
                    }
                });
                channel.BasicConsume(replyQueueName, true, consumer);

            }

            public Task<long> GetLastLedgerIndex()
            {
                var corrId = Guid.NewGuid().ToString();
                var props = channel.CreateBasicProperties();
                props.ReplyTo = replyQueueName;
                props.CorrelationId = corrId;
                props.Expiration = "10000";

                var message = IoC.Resolve<IJsonSerializer>().Serialize(new GetLastLedgerIndexMessage());
                var messageBytes = Encoding.UTF8.GetBytes(message);
                channel.BasicPublish(RIPPLE_VALIDATE_EXCHANGE_NAME, string.Empty, props, messageBytes);
                var ts = new TaskCompletionSource<long>();

                requsetDic.TryAdd(corrId, ts);

                Task.Factory.StartNew(() =>
                {
                    Task.Delay(10000).ContinueWith((t) =>
                    {
                        TaskCompletionSource<long> tsGet;
                        if (requsetDic.TryRemove(corrId, out tsGet))
                        {
                            tsGet.SetResult(-1); 
                        }
                    });
                });
                return ts.Task;
            }
        }

        private class ValidatorRpcClient
        {
            private readonly IModel channel;
            private readonly DefaultBasicConsumer consumer;
            private readonly string replyQueueName;
            private IConnection connection;
            private ConcurrentDictionary<string, TaskCompletionSource<int>> requsetDic = new ConcurrentDictionary<string, TaskCompletionSource<int>>();


            public ValidatorRpcClient(IConnection connection)
            {
                this.connection = connection;
                channel = connection.CreateModel();
                replyQueueName = channel.QueueDeclare(RIPPLE_VALIDATE_QUEUE_REPLY, true, false, false, null);
                channel.ExchangeDeclare(RIPPLE_VALIDATE_EXCHANGE_NAME, ExchangeType.Direct);
                channel.QueueDeclare(RIPPLE_VALIDATE_QUEUE, true, false, false, null);
                channel.QueueBind(RIPPLE_VALIDATE_QUEUE, RIPPLE_VALIDATE_EXCHANGE_NAME, "");
                consumer = new ValidatorMessageConsumer(channel, (p, b) =>
                {
                    TaskCompletionSource<int> ts;
                    requsetDic.TryRemove(p.CorrelationId, out ts);

                    if (ts != null)
                    {
                        try
                        {
                            var result = Convert.ToInt32(Encoding.UTF8.GetString(b));
                            ts.SetResult(result);
                        }
                        catch
                        {
                            ts.SetResult(-1);
                        }
                    }
                });
                channel.BasicConsume(replyQueueName, true, consumer);
            }

            /// <summary>
            /// </summary>
            /// <param name="txid"></param>
            /// <returns>-1 超时，1 true, 0 false</returns>
            public Task<int> ValidateTx(string txid)
            {
                var corrId = Guid.NewGuid().ToString();
                var props = channel.CreateBasicProperties();
                props.ReplyTo = replyQueueName;
                props.CorrelationId = corrId;
                props.Expiration = "10000";

                var message = IoC.Resolve<IJsonSerializer>().Serialize(new ValidateTxMessage(txid));
                var messageBytes = Encoding.UTF8.GetBytes(message);
                channel.BasicPublish(RIPPLE_VALIDATE_EXCHANGE_NAME, string.Empty, props, messageBytes);

                var ts = new TaskCompletionSource<int>();

                requsetDic.TryAdd(corrId, ts);

                Task.Factory.StartNew(() =>
                {
                    Task.Delay(10000).ContinueWith((t) =>
                    {
                        TaskCompletionSource<int> tsGet;
                        if (requsetDic.TryRemove(corrId, out tsGet))
                        {
                            tsGet.SetResult(-1);
                        }
                    });
                });
                return ts.Task;
            }
        }

        #region Consumer

        private class ValidatorMessageConsumer : DefaultBasicConsumer
        {
            private Action<IBasicProperties, byte[]> action;

            public ValidatorMessageConsumer(IModel model, Action<IBasicProperties, byte[]> task)
                : base(model)
            {
                this.action = task;
            }

            public override void HandleBasicDeliver(string consumerTag, ulong deliveryTag, bool redelivered, string exchange, string routingKey, IBasicProperties properties, byte[] body)
            {
                var messageBody = Encoding.UTF8.GetString(body);

                try
                {
                    this.action(properties, body);
                }
                catch (Exception ex)
                {
                    Log.Error("ValidatorMessageConsumer Deserialize Message Exception.", ex);
                }
            }
        }
        #endregion

        #endregion

        #region message

        [Serializable]
        private class GetLastLedgerIndexMessage
        {
            public string Command
            {
                get { return "GetLastCompleteLedgerIndex"; }
            }
        }

        [Serializable]
        private class ValidateTxMessage
        {
            public ValidateTxMessage(string txId)
            {
                this.TxId = txId;
            }

            public string Command
            {
                get { return "ValidateTx"; }
            }

            public string TxId { get; set; }
        }

        #endregion

        #region private method

        private static MySqlConnection OpenConnection()
        {
            var connection = new MySqlConnection(MysqlConnectionString);
            connection.Open();
            return connection;
        }

        //获取已提交了4分钟，仍然没有结果的数据
        //已提交但从未submit过的，不会重复检测并再次提交，可防止多发IOU
        private static IEnumerable<TaobaoAutoDeposit> GetLoseTransaction()
        {
            const string sql =
                "SELECT tid,amount,has_buyer_message,taobao_status,ripple_address,ripple_status,txid,memo,first_submit_at,tx_lastLedgerSequence,retry_Counter" +
                "  FROM taobao " +
                " WHERE taobao_status=@taobao_status AND ripple_status=@ripple_status AND first_submit_at<@submit_at";
            try
            {
                using (var conn = OpenConnection())
                {
                    var tradesInDb = conn.Query<TaobaoAutoDeposit>(sql, new
                    {
                        taobao_status = "WAIT_SELLER_SEND_GOODS",
                        ripple_status = RippleTransactionStatus.Submited,
                        submit_at = DateTime.Now.AddMinutes(-3)
                    });

                    return tradesInDb;
                }
            }
            catch (Exception ex)
            {
                Log.Error("GetLoseTransaction Exception", ex);
                return null;
            }
        }

        private static int MarkTxSuccess(long tid)
        {
            const string sql =
                "UPDATE taobao SET ripple_status=@ripple_status_new " +
                " WHERE tid=@tid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql,
                        new
                        {
                            tid,
                            ripple_status_new = RippleTransactionStatus.Successed,
                            taobao_status = "WAIT_SELLER_SEND_GOODS",
                            ripple_status_old = RippleTransactionStatus.Submited
                        });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }

        //标记为初始状态，可自动进入下次处理过程
        private static int MarkTxAsInitForNextProccesLoop(long tid)
        {
            const string sql =
                "UPDATE taobao SET ripple_status=@ripple_status_new,txid='',tx_lastLedgerSequence=0,first_submit_at=null" +
                " WHERE tid=@tid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql,
                        new
                        {
                            tid,
                            ripple_status_new = RippleTransactionStatus.Init,
                            taobao_status = "WAIT_SELLER_SEND_GOODS",
                            ripple_status_old = RippleTransactionStatus.Submited
                        });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxAsInitForNextProccesLoop Exception", ex);
                return 0;
            }
        }

        #endregion
    }
}